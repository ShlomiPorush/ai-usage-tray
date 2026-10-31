using System.Net;
using System.Text.Json.Nodes;
using costats.App.Services;
using costats.Application.Diagnostics;
using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Analytics;
using costats.Core.Pulse;
using costats.Core.Remote;
using costats.Core.RemoteView;
using costats.Infrastructure.Analytics;
using costats.Infrastructure.Providers;
using costats.Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Serilog;
using Xunit;

namespace costats.Core.Tests.Diagnostics;

/// <summary>
/// Runs the components that log provider traffic through their failure and
/// success paths with known fake secrets, then checks that no secret reached
/// any log entry: not the message, not a structured value, not an exception.
/// The fakes deliberately echo the secrets back in response bodies and error
/// text, the places a careless log line would pick them up from.
/// </summary>
public sealed class SecretLoggingTests : IDisposable
{
    private const string ClaudeAccessToken = "sk-ant-oat01-FAKEACCESS0123456789abcdefghijklmnop";
    private const string ClaudeRefreshToken = "sk-ant-ort01-FAKEREFRESH0123456789abcdefghijklmno";
    private const string ZaiCodingKey = "zai-coding-FAKE0123456789abcdefghijklmnop.secretpart";
    private const string ZaiStandardKey = "zai-standard-FAKE0123456789abcdefghijklmnop.secret2";
    private const string CopilotToken = "ghu_FAKECOPILOT0123456789abcdefghijklmnopqr";
    private const string CodexToken = "eyJhbGciOiJSUzI1NiJ9FAKECODEX0123456789abcdefghijklmnop";
    private const string SigningKey = "signing-FAKE0123456789abcdefghijklmnopqrstuv";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ai-usage-tray-secret-logging-tests", Guid.NewGuid().ToString("N"));

    public SecretLoggingTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Temp cleanup is best effort.
        }
    }

    private static void AssertNoSecrets(string logText, params string[] secrets)
    {
        Assert.False(string.IsNullOrWhiteSpace(logText), "The scenario logged nothing, so it proves nothing.");
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, logText, StringComparison.Ordinal);
            // A secret cut in half is still a leak.
            Assert.DoesNotContain(secret[..(secret.Length / 2)], logText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Claude_fetcher_never_logs_its_tokens()
    {
        var configDir = Path.Combine(_root, "claude-2");
        Directory.CreateDirectory(configDir);
        var expiresAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds();
        await File.WriteAllTextAsync(Path.Combine(configDir, ".credentials.json"), $$$"""
            {"claudeAiOauth":{"accessToken":"{{{ClaudeAccessToken}}}","refreshToken":"{{{ClaudeRefreshToken}}}","expiresAt":{{{expiresAt}}}}}
            """);

        var logger = new CapturingLogger();
        var authorizationSeen = false;
        var calls = 0;
        var handler = new FakeHandler(request =>
        {
            authorizationSeen |= request.Headers.Authorization?.Parameter == ClaudeAccessToken;
            if (Interlocked.Increment(ref calls) == 1)
            {
                // A server that echoes the credential back.
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent($"{{\"error\":\"bad token {ClaudeAccessToken}\"}}")
                };
            }

            throw new HttpRequestException($"connection reset while sending Bearer {ClaudeAccessToken}");
        });

        using (var fetcher = new ClaudeOAuthUsageFetcher(configDir, handler, logger))
        {
            await fetcher.FetchAsync(CancellationToken.None);
        }

        using (var second = new ClaudeOAuthUsageFetcher(configDir, handler, logger))
        {
            await second.FetchAsync(CancellationToken.None);
        }

        Assert.True(authorizationSeen);
        Assert.Contains("HTTP 500", logger.AllText, StringComparison.Ordinal);
        Assert.Contains("HttpRequestException", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, logger.AllText, StringComparison.OrdinalIgnoreCase);
        AssertNoSecrets(logger.AllText, ClaudeAccessToken, ClaudeRefreshToken);
    }

    [Fact]
    public async Task Zai_fetcher_never_logs_its_keys()
    {
        var logger = new CapturingLogger();
        var calls = 0;
        var handler = new FakeHandler(request =>
        {
            var key = request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : "";
            if (Interlocked.Increment(ref calls) == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent($"{{\"msg\":\"invalid key {key}\"}}")
                };
            }

            throw new HttpRequestException($"failed with Authorization: {key}");
        });

        using var fetcher = new ZaiUsageFetcher(handler, logger);
        await fetcher.FetchAsync(ZaiCodingKey, ZaiStandardKey, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Contains("HTTP 401", logger.AllText, StringComparison.Ordinal);
        AssertNoSecrets(logger.AllText, ZaiCodingKey, ZaiStandardKey);
    }

    [Fact]
    public async Task Copilot_fetcher_never_logs_its_token()
    {
        var logger = new CapturingLogger<CopilotUsageFetcher>();
        var calls = 0;
        var handler = new FakeHandler(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new HttpRequestException($"reset while sending token {CopilotToken}");
            }

            return new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent(CopilotToken)
            };
        });

        using var fetcher = new CopilotUsageFetcher(logger, handler);
        await fetcher.FetchAsync(CopilotToken, CancellationToken.None);

        Assert.Contains("HTTP 502", logger.AllText, StringComparison.Ordinal);
        // The retry warning carries the exception. Its message here contains
        // the token only because this fake put it there; real HttpClient
        // messages do not. The debug lines must still be clean.
        var debugText = string.Join("\n", logger.Entries.Where(entry => entry.Level == LogLevel.Debug).Select(entry => entry.Text));
        AssertNoSecrets(debugText, CopilotToken);
    }

    [Fact]
    public async Task Codex_client_logs_methods_and_outcomes_but_no_response_content()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var codexHome = Path.Combine(_root, "codex-2");
        Directory.CreateDirectory(codexHome);
        // A fake app-server: answers account/read with a token-shaped field
        // and rate limits with an error that names a token.
        var fakeCodex = Path.Combine(_root, "fake-codex.cmd");
        // cmd's "set /p" swallows a whole piped chunk, so the script answers
        // up front and then just stays alive until the client stops it.
        await File.WriteAllTextAsync(fakeCodex, string.Join("\r\n",
            "@echo off",
            $"echo {{\"id\":3,\"result\":{{\"account\":{{\"type\":\"chatgpt\",\"email\":\"person@example.com\",\"accessToken\":\"{CodexToken}\"}},\"requiresOpenaiAuth\":false}}}}",
            $"echo {{\"id\":2,\"error\":{{\"code\":-32000,\"message\":\"upstream rejected token={CodexToken}\"}}}}",
            "set /p request=",
            "ping -n 6 127.0.0.1 >nul",
            ""));

        var logger = new CapturingLogger<CodexAppServerClient>();
        using var client = new CodexAppServerClient(fakeCodex, TimeSpan.FromSeconds(10), logger);

        await client.FetchAsync(codexHome, refreshToken: true, CancellationToken.None);

        var text = logger.AllText;
        Assert.Contains("account/read", text, StringComparison.Ordinal);
        Assert.Contains("account/rateLimits/read answered an", text, StringComparison.Ordinal);
        Assert.Contains("refreshToken True", text, StringComparison.Ordinal);
        Assert.DoesNotContain("person@example.com", text, StringComparison.Ordinal);
        // The executable's path is logged on purpose (which Codex ran); the
        // account folder is only ever named, never given as a path.
        Assert.DoesNotContain(codexHome, text, StringComparison.OrdinalIgnoreCase);
        AssertNoSecrets(text, CodexToken);
    }

    [Fact]
    public async Task Codex_client_reports_a_missing_executable_as_a_warning()
    {
        var codexHome = Path.Combine(_root, "codex-3");
        var logger = new CapturingLogger<CodexAppServerClient>();
        using var client = new CodexAppServerClient(
            Path.Combine(_root, "no-such-codex.exe"),
            TimeSpan.FromSeconds(5),
            logger);

        var result = await client.FetchAsync(codexHome, refreshToken: false, CancellationToken.None);

        Assert.Null(result);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Text.Contains("codex-3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Settings_saves_log_changed_names_but_no_values()
    {
        var logger = new CapturingLogger();
        var store = new JsonSettingsStore(credentialVault: null, basePath: _root) { Logger = logger };
        var settings = await store.LoadAsync(CancellationToken.None);
        var writeId = RemoteViewIds.MintWriteId();

        settings.RefreshMinutes = 9;
        settings.RemoteViewId = writeId;
        settings.ZAiCodingApiKey = ZaiCodingKey;
        await store.SaveAsync(settings, CancellationToken.None);

        var text = logger.AllText;
        Assert.Contains("refreshMinutes", text, StringComparison.Ordinal);
        Assert.Contains("remoteViewId", text, StringComparison.Ordinal);
        Assert.Contains("Z.AI coding key changed: True", text, StringComparison.Ordinal);
        AssertNoSecrets(text, ZaiCodingKey, writeId);
    }

    [Fact]
    public void Changed_setting_names_compare_values_without_returning_them()
    {
        var before = (JsonObject)JsonNode.Parse("""{"a":1,"b":"x","c":[1,2]}""")!;
        var after = (JsonObject)JsonNode.Parse("""{"a":1,"b":"y","c":[1,2],"d":true}""")!;

        Assert.Equal(["b", "d"], JsonSettingsStore.ChangedSettingNames(before, after));
        Assert.Empty(JsonSettingsStore.ChangedSettingNames(before, before));
    }

    [Fact]
    public async Task Remote_view_uploader_never_logs_the_write_id_read_id_or_signing_key()
    {
        var writeId = RemoteViewIds.MintWriteId();
        var readId = RemoteViewIds.DeriveReadId(writeId);
        var settings = new AppSettings
        {
            RemoteViewEnabled = true,
            RemoteViewId = writeId,
            DefaultRemoteViewUploadUrl = "https://relay.example.test",
            DefaultRemoteViewPageUrl = "https://viewer.example.test",
            DefaultRemoteViewSigningKey = SigningKey
        };

        var sink = new CapturingSink();
        using var log = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        var requests = 0;
        var handler = new FakeHandler(request =>
        {
            var attempt = Interlocked.Increment(ref requests);
            return attempt switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent($"no snapshot for {request.RequestUri}")
                },
                2 => throw new HttpRequestException($"connection lost for {request.RequestUri}"),
                _ => new HttpResponseMessage(HttpStatusCode.NoContent)
            };
        });

        using var uploader = new RemoteViewUploader(
            new FakePulseOrchestrator(),
            [],
            new FakeAccountSources(),
            settings,
            handler,
            log);

        var state = State();
        uploader.OnNext(state);
        await WaitForAsync(() => sink.AllText.Contains("HTTP 500", StringComparison.Ordinal));
        // Let the upload release its in-flight slot before the next pulse.
        await Task.Delay(100);
        uploader.OnNext(state);
        await WaitForAsync(() => sink.AllText.Contains("2 in a row", StringComparison.Ordinal));
        await Task.Delay(100);
        uploader.OnNext(state);
        await WaitForAsync(() => sink.AllText.Contains("HTTP 204", StringComparison.Ordinal));
        await uploader.DeleteAsync(writeId);

        var text = sink.AllText;
        Assert.Contains("relay.example.test", text, StringComparison.Ordinal);
        AssertNoSecrets(text, writeId, readId, SigningKey);
    }

    [Fact]
    public async Task Remote_usage_sharing_never_logs_ids_urls_or_report_content()
    {
        const string Nickname = "Nick-SENTINEL-1234";
        const string Model = "model-SENTINEL-5678";
        var writeId = RemoteViewIds.MintWriteId();
        var readId = RemoteViewIds.DeriveReadId(writeId);
        var settings = new AppSettings
        {
            RemoteViewEnabled = true,
            RemoteViewShareUsage = true,
            RemoteViewId = writeId,
            DefaultRemoteViewUploadUrl = "https://relay.example.test",
            DefaultRemoteViewPageUrl = "https://viewer.example.test",
            DefaultRemoteViewSigningKey = SigningKey
        };

        var sink = new CapturingSink();
        using var log = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        var clock = new SteppedClock(new DateTimeOffset(2026, 10, 28, 8, 0, 0, TimeSpan.Zero));
        var puts = 0;
        var deletes = 0;
        var handler = new FakeHandler(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                return Interlocked.Increment(ref puts) switch
                {
                    // A relay that echoes the address and the body back.
                    1 => new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent($"bad {request.RequestUri} {request.Content!.ReadAsStringAsync().Result}")
                    },
                    2 => throw new HttpRequestException($"connection lost for {request.RequestUri}"),
                    3 => new HttpResponseMessage(HttpStatusCode.Conflict)
                    {
                        Content = new StringContent($"{{\"error\":\"stale_write\",\"detail\":\"{request.RequestUri}\"}}")
                    },
                    _ => new HttpResponseMessage(HttpStatusCode.NoContent)
                };
            }

            return Interlocked.Increment(ref deletes) == 1
                ? new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent($"no {request.RequestUri}") }
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        var analytics = new FixedUsageAnalytics(new RemoteUsageSource(
            [new UsageSample(clock.GetUtcNow().AddHours(-2), UsageProviderKind.Claude, "claude-1", Model, new UsageTokens { UncachedInputTokens = 100, OutputTokens = 10 })],
            ModelPricingTable.Default,
            [new RemoteUsageAccountSource("claude-1", UsageProviderKind.Claude, Nickname)]));

        using var uploader = new RemoteViewUploader(
            new FakePulseOrchestrator(),
            [],
            new FakeAccountSources(),
            settings,
            handler,
            log,
            analytics,
            settingsStore: null,
            clock);

        foreach (var wait in new[] { 0, 10, 20, 10 })
        {
            clock.Advance(TimeSpan.FromMinutes(wait));
            await uploader.Sharing.RunCycleAsync();
        }

        settings.RemoteViewShareUsage = false;
        await uploader.Sharing.Reconcile()!;
        clock.Advance(TimeSpan.FromMinutes(10));
        await uploader.Sharing.RetryPendingDeletesAsync();
        await uploader.DeleteAsync(writeId);
        await uploader.Queue.WhenIdleAsync();

        Assert.Equal(4, puts);
        Assert.Equal(3, deletes);
        var text = sink.AllText;
        Assert.Contains("relay.example.test", text, StringComparison.Ordinal);
        Assert.Contains("usage upload", text, StringComparison.Ordinal);
        Assert.Contains("stale_write", text, StringComparison.Ordinal);
        AssertNoSecrets(text, writeId, readId, SigningKey, Nickname, Model);
    }

    [Theory]
    [InlineData("refresh token expired", "refresh token expired")]
    [InlineData("Authorization: Bearer abc.def", "Authorization=[redacted]")]
    [InlineData("call failed for Bearer xyz", "call failed for Bearer [redacted]")]
    [InlineData("{\"access_token\":\"short\"}", "{\"access_token=[redacted]\"}")]
    [InlineData("System.Net.Http.HttpRequestException: host not found", "System.Net.Http.HttpRequestException: host not found")]
    public void Redaction_masks_credential_values_and_keeps_ordinary_text(string input, string expected)
    {
        Assert.Equal(expected, LogRedaction.Redact(input));
    }

    [Fact]
    public async Task Antigravity_runner_and_source_never_log_the_email_or_payload_text()
    {
        // Stand-in for agy: cmd.exe copies a payload that holds an email, the
        // way agy's statusline would. The real agy is never started.
        const string email = "secret.person@example.com";
        const string modelName = "Gemini 3.8 Flash (High) private-model-marker";
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var success = Path.Combine(_root, "agy success.json");
        var unsupported = Path.Combine(_root, "agy unsupported.json");
        await File.WriteAllTextAsync(success, """
            {"agent_state":"idle","plan_tier":"Google AI Plus","email":"EMAIL","model":{"id":"MODEL"},
             "quota":{"gemini-weekly":{"remaining_fraction":0.4,"reset_time":"2026-11-04T12:58:07Z","reset_in_seconds":478030}}}
            """.Replace("EMAIL", email, StringComparison.Ordinal).Replace("MODEL", modelName, StringComparison.Ordinal));
        await File.WriteAllTextAsync(unsupported, """
            {"agent_state":"idle","email":"EMAIL","model":{"id":"MODEL"},
             "quota":{"3p-weekly":{"remaining_fraction":0.4,"reset_time":"2026-11-04T12:58:07Z"}}}
            """.Replace("EMAIL", email, StringComparison.Ordinal).Replace("MODEL", modelName, StringComparison.Ordinal));

        var logger = new CapturingLogger();
        var payload = success;
        var runner = new costats.Infrastructure.Antigravity.AgyProbeRunner(logger, new costats.Infrastructure.Antigravity.AgyProbeRunnerOptions
        {
            BasePath = Path.Combine(_root, "antigravity"),
            ResolveExecutable = () => cmd,
            CommandLine = (_, _) => $"\"{cmd}\" /d /c copy /y \"{payload}\" payload.json >nul & ping -n 30 127.0.0.1 >nul",
            Deadline = TimeSpan.FromSeconds(2)
        });
        var settings = new AppSettings { AntigravityEnabled = true, AntigravityMinIntervalMinutes = 5 };
        var sourceLogger = new CapturingLogger<costats.Infrastructure.Antigravity.AntigravityAgySource>();
        var source = new costats.Infrastructure.Antigravity.AntigravityAgySource(settings, runner, sourceLogger);

        var read = await source.ReadAsync(CancellationToken.None);
        Assert.NotNull(read.Usage);

        // Unsupported and failure paths warn; run them through the runner directly.
        payload = unsupported;
        var request = new costats.Infrastructure.Antigravity.AgyProbeRequest(TimeSpan.FromMinutes(5));
        var schedule = Path.Combine(_root, "antigravity", "AIUsageTray", "antigravity-probe", "schedule.json");
        File.Delete(schedule);
        Assert.Equal(costats.Infrastructure.Antigravity.AgyProbeStatus.UnsupportedFormat, (await runner.RunAsync(request, CancellationToken.None)).Status);
        File.Delete(schedule);
        Assert.Equal(costats.Infrastructure.Antigravity.AgyProbeStatus.UnsupportedFormat, (await runner.RunAsync(request, CancellationToken.None)).Status);

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
        AssertNoSecrets(logger.AllText + "\n" + sourceLogger.AllText, email, modelName, "remaining_fraction", "Google AI Plus");
        Assert.DoesNotContain(email, read.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Redaction_masks_long_token_runs_and_ids_in_urls()
    {
        var writeId = RemoteViewIds.MintWriteId();

        var redacted = LogRedaction.Redact($"PUT https://relay.example.test/u/{writeId} with {CodexToken} failed")!;

        Assert.DoesNotContain(writeId, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(CodexToken[..20], redacted, StringComparison.Ordinal);
        Assert.Contains("relay.example.test", redacted, StringComparison.Ordinal);
        Assert.True(LogRedaction.Redact(new string('a', 1000) + " x")!.Length <= LogRedaction.MaxLength + 3);
    }

    private static PulseState State()
    {
        var now = DateTimeOffset.UtcNow;
        var usage = new UsagePulse("claude:claude-1", now, 40, 100, 20, 100, null, null);
        var reading = new ProviderReading(
            usage,
            new IdentityCard("claude:claude-1", "Claude", "person@example.com", null, "Max", "test"),
            "ok",
            now,
            ReadingConfidence.High,
            ReadingSource.Api,
            ProviderAuthenticationState.Authenticated);
        return new PulseState(
            new Dictionary<string, ProviderReading> { ["claude:claude-1"] = reading },
            now,
            [],
            false,
            RefreshTrigger.Scheduled);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition(), "Timed out waiting for the expected log entry.");
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class SteppedClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>Usage analytics that only answers the remote usage question.</summary>
    private sealed class FixedUsageAnalytics(RemoteUsageSource source) : IUsageAnalyticsService
    {
        public TimeZoneInfo TimeZone => TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

        public event EventHandler? HistoryChanged { add { } remove { } }

        public Task<RemoteUsageSource> GetRemoteUsageSourceAsync(CancellationToken cancellationToken = default) => Task.FromResult(source);

        public Task<UsageReport> GetReportAsync(UsageDateRange range, IReadOnlyCollection<string>? accountIds = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(UsageReport.Empty);

        public Task<UsageReport> GetWindowReportAsync(UsageTimeWindow window, IReadOnlyCollection<string>? accountIds = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(UsageReport.Empty);

        public Task<IReadOnlyList<UsageAccountInfo>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UsageAccountInfo>>([]);

        public Task<UsageActivityDays?> GetActivityDaysAsync(IReadOnlyCollection<string>? accountIds = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<UsageActivityDays?>(null);

        public UsageCacheInfo GetCacheInfo() => new(0, 0);

        public Task<UsageCacheInfo> ClearCacheAsync(CancellationToken cancellationToken = default) => Task.FromResult(GetCacheInfo());

        public void Invalidate()
        {
        }

        public Task SyncHistoryAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> DeleteHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

        public UsageHistoryInfo GetHistoryInfo() => UsageHistoryInfo.Empty;
    }

    private sealed class FakeAccountSources : IAccountSourceRegistry
    {
        public IReadOnlyList<ISignalSource> Current => [];

        public void Reload()
        {
        }
    }

    private sealed class FakePulseOrchestrator : IPulseOrchestrator
    {
        public IObservable<PulseState> PulseStream { get; } = new PulseBroadcaster();
        public PulseState? CurrentState => null;
        public Task RefreshOnceAsync(RefreshTrigger trigger, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RefreshIfStaleAsync(TimeSpan maxAge, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RefreshProviderAsync(string providerId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ProviderRefreshResult> RefreshProviderForVerificationAsync(string providerId, CancellationToken cancellationToken) =>
            Task.FromResult(ProviderRefreshResult.Failure());
        public bool IsFullRefreshStale(TimeSpan maxAge) => false;
        public void RepublishLastState()
        {
        }
        public void UpdateRefreshInterval(TimeSpan interval)
        {
        }
    }
}
