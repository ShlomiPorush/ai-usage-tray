using System.Net;
using System.Net.Http;
using costats.App.Services;
using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Antigravity;
using costats.Core.Pulse;
using costats.Core.Remote;
using costats.Core.RemoteView;
using costats.Core.Tests.Antigravity;
using Serilog;
using Xunit;

namespace costats.Core.Tests.Services;

/// <summary>
/// A quota snapshot built before remote view was turned off, or before its
/// link changed, must never reach the relay after the snapshot was deleted.
/// An account turned off before its snapshot was sent leaves the remote view.
/// </summary>
public sealed class RemoteViewUploaderTests
{
    private static readonly string WriteId = RemoteViewIds.MintWriteId();

    [Fact]
    public async Task A_snapshot_built_before_remote_view_was_turned_off_is_dropped_after_the_delete()
    {
        var (uploader, settings, requests) = Uploader();
        using var _ = uploader;

        // Built, then stalled before reaching the queue.
        var upload = uploader.PrepareSnapshotUpload(State(), DateTimeOffset.UtcNow);
        settings.RemoteViewEnabled = false;
        Assert.True(await uploader.DeleteAsync(WriteId));
        await uploader.EnqueueSnapshotUpload(upload);
        await uploader.Queue.WhenIdleAsync();

        Assert.Equal([$"DELETE /u/{WriteId}"], requests);
    }

    [Fact]
    public async Task A_snapshot_built_for_a_retired_link_is_dropped()
    {
        var (uploader, settings, requests) = Uploader();
        using var _ = uploader;

        var upload = uploader.PrepareSnapshotUpload(State(), DateTimeOffset.UtcNow);
        settings.RemoteViewId = RemoteViewIds.MintWriteId();
        await uploader.EnqueueSnapshotUpload(upload);

        Assert.Empty(requests);
    }

    [Fact]
    public async Task A_snapshot_whose_remote_view_is_unchanged_is_sent()
    {
        var (uploader, _, requests) = Uploader();
        using var _ = uploader;

        await uploader.EnqueueSnapshotUpload(uploader.PrepareSnapshotUpload(State(), DateTimeOffset.UtcNow));

        Assert.Equal([$"PUT /u/{WriteId}"], requests);
    }

    [Fact]
    public void An_enabled_Antigravity_account_is_published_with_its_windows_and_no_email()
    {
        var (uploader, settings, _) = Uploader();
        using var _ = uploader;
        settings.AntigravityEnabled = true;
        settings.PrimaryAccountId = "antigravity";

        var snapshot = uploader.PrepareSnapshotUpload(StateWithAntigravity(), DateTimeOffset.UtcNow).Snapshot;
        var json = Serialize(snapshot);

        Assert.Equal("antigravity", snapshot.Primary);
        var account = Assert.Single(snapshot.Accounts, entry => entry.Id == "antigravity");
        Assert.Equal("antigravity", account.Provider);
        Assert.Equal("Google AI Plus", account.Plan);
        Assert.Contains(account.Windows, window => window.Label == "Weekly" && window.Scope is null && window.UsedPercent == 100);
        Assert.Contains(account.Windows, window => window.Label == "Weekly" && window.Scope == "Claude & GPT");
        Assert.DoesNotContain(AgyFixtures.Email, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_turned_off_Antigravity_account_is_not_published_not_even_as_the_primary_account()
    {
        var (uploader, settings, _) = Uploader();
        using var _ = uploader;
        settings.AntigravityEnabled = false;
        settings.PrimaryAccountId = "antigravity";

        var snapshot = uploader.PrepareSnapshotUpload(StateWithAntigravity(), DateTimeOffset.UtcNow).Snapshot;

        Assert.Equal(["claude:claude-1"], snapshot.Accounts.Select(account => account.Id));
        Assert.Null(snapshot.Primary);
        Assert.DoesNotContain("antigravity", Serialize(snapshot), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_account_without_a_source_is_not_published_from_an_older_state()
    {
        var (uploader, settings, _) = Uploader();
        using var _ = uploader;
        settings.PrimaryAccountId = "claude:deleted";

        // A state from before the account was deleted still carries its reading.
        var state = State();
        var providers = new Dictionary<string, ProviderReading>(state.Providers)
        {
            ["claude:deleted"] = state.Providers["claude:claude-1"]
        };
        var snapshot = uploader.PrepareSnapshotUpload(state with { Providers = providers }, DateTimeOffset.UtcNow).Snapshot;

        Assert.Equal(["claude:claude-1"], snapshot.Accounts.Select(account => account.Id));
        Assert.Null(snapshot.Primary);
    }

    [Fact]
    public async Task A_queued_snapshot_drops_an_account_turned_off_before_it_was_sent()
    {
        var (uploader, settings, bodies, _) = RecordingUploader();
        using var _ = uploader;
        settings.AntigravityEnabled = true;
        settings.PrimaryAccountId = "antigravity";

        var upload = uploader.PrepareSnapshotUpload(StateWithAntigravity(), DateTimeOffset.UtcNow);
        settings.AntigravityEnabled = false;
        await uploader.EnqueueSnapshotUpload(upload);

        var body = Assert.Single(bodies);
        Assert.DoesNotContain("antigravity", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("claude:claude-1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turning_an_account_off_during_an_upload_publishes_again_without_it()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (uploader, settings, bodies, sent) = RecordingUploader(holdFirstPut: release.Task);
        using var _ = uploader;
        settings.AntigravityEnabled = true;

        uploader.OnNext(StateWithAntigravity());
        await sent.WaitForAsync(1);

        // The first PUT is on the wire; the change arrives while it runs.
        settings.AntigravityEnabled = false;
        uploader.PublishAfterAccountChange();
        release.SetResult();

        await sent.WaitForAsync(2);
        Assert.Contains("antigravity", bodies[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("antigravity", bodies[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_republish_after_an_upload_carries_the_newest_refresh()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (uploader, settings, bodies, sent) = RecordingUploader(holdFirstPut: release.Task);
        using var _ = uploader;
        settings.AntigravityEnabled = true;

        uploader.OnNext(StateWithAntigravity());
        await sent.WaitForAsync(1);

        // A refresh lands while the first PUT runs: it is stored, not sent.
        var newer = StateWithAntigravity();
        var claude = newer.Providers["claude:claude-1"];
        var providers = new Dictionary<string, ProviderReading>(newer.Providers)
        {
            ["claude:claude-1"] = claude with { Usage = claude.Usage! with { SessionUsed = 77 } }
        };
        uploader.OnNext(newer with { Providers = providers });
        uploader.PublishAfterAccountChange();
        release.SetResult();

        await sent.WaitForAsync(2);
        Assert.Contains("\"usedPercent\":20", bodies[0], StringComparison.Ordinal);
        Assert.Contains("\"usedPercent\":77", bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turning_an_account_off_when_idle_publishes_at_once_despite_the_throttle()
    {
        var (uploader, settings, bodies, sent) = RecordingUploader();
        using var _ = uploader;
        settings.AntigravityEnabled = true;

        uploader.OnNext(StateWithAntigravity());
        await sent.WaitForAsync(1);
        await uploader.Queue.WhenIdleAsync();

        settings.AntigravityEnabled = false;
        uploader.PublishAfterAccountChange();

        await sent.WaitForAsync(2);
        Assert.DoesNotContain("antigravity", bodies[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_published_primary_account_is_still_named()
    {
        var (uploader, settings, _) = Uploader();
        using var _ = uploader;
        settings.PrimaryAccountId = "claude:claude-1";

        var snapshot = uploader.PrepareSnapshotUpload(State(), DateTimeOffset.UtcNow).Snapshot;

        Assert.Equal("claude:claude-1", snapshot.Primary);
    }

    private static (RemoteViewUploader Uploader, AppSettings Settings, List<string> Requests) Uploader()
    {
        var settings = new AppSettings
        {
            RemoteViewEnabled = true,
            RemoteViewId = WriteId,
            DefaultRemoteViewUploadUrl = "https://relay.example.test",
            DefaultRemoteViewPageUrl = "https://viewer.example.test"
        };
        var requests = new List<string>();
        var handler = new Handler(request =>
        {
            lock (requests)
            {
                requests.Add($"{request.Method.Method} {request.RequestUri!.AbsolutePath}");
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        var uploader = new RemoteViewUploader(
            new Orchestrator(),
            Sources(),
            new NoAccountSources(),
            settings,
            handler,
            new LoggerConfiguration().CreateLogger());
        return (uploader, settings, requests);
    }

    /// <summary>
    /// An uploader whose relay records every PUT body. With
    /// <paramref name="holdFirstPut"/>, the first PUT does not answer until
    /// that task completes, so a test can act while an upload is running.
    /// </summary>
    private static (RemoteViewUploader Uploader, AppSettings Settings, List<string> Bodies, SentPuts Sent) RecordingUploader(
        Task? holdFirstPut = null)
    {
        var settings = new AppSettings
        {
            RemoteViewEnabled = true,
            RemoteViewId = WriteId,
            DefaultRemoteViewUploadUrl = "https://relay.example.test",
            DefaultRemoteViewPageUrl = "https://viewer.example.test"
        };
        var bodies = new List<string>();
        var sent = new SentPuts();
        var handler = new AsyncHandler(async request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                var body = await request.Content!.ReadAsStringAsync();
                int count;
                lock (bodies)
                {
                    bodies.Add(body);
                    count = bodies.Count;
                }

                sent.Signal(count);
                if (count == 1 && holdFirstPut is not null)
                {
                    await holdFirstPut;
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        var uploader = new RemoteViewUploader(
            new Orchestrator(),
            Sources(),
            new NoAccountSources(),
            settings,
            handler,
            new LoggerConfiguration().CreateLogger());
        return (uploader, settings, bodies, sent);
    }

    /// <summary>
    /// The claude account of <see cref="State"/> plus an Antigravity reading
    /// parsed from the sanitized agy payload, which carries a placeholder email.
    /// </summary>
    private static PulseState StateWithAntigravity()
    {
        var now = DateTimeOffset.UtcNow;
        var quota = AgyQuotaParser.Parse(AgyFixtures.RealPayloadWithPlaceholderEmail).Reading!;
        var providers = new Dictionary<string, ProviderReading>(State().Providers)
        {
            ["antigravity"] = new ProviderReading(
                quota.ToUsagePulse("antigravity", now),
                new IdentityCard("antigravity", "Antigravity", null, null, quota.PlanTier, "agy"),
                "Updated 08:00",
                now,
                ReadingConfidence.High,
                ReadingSource.Cli)
        };
        return new PulseState(providers, now, [], false, RefreshTrigger.Scheduled);
    }

    private static string Serialize(RemoteSnapshot snapshot) =>
        System.Text.Json.JsonSerializer.Serialize(
            snapshot,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    /// <summary>Lets a test wait, with a time limit, until the relay has received a number of PUTs.</summary>
    private sealed class SentPuts
    {
        private readonly object _gate = new();
        private readonly List<(int Count, TaskCompletionSource Done)> _waiters = [];
        private int _count;

        public void Signal(int count)
        {
            lock (_gate)
            {
                _count = count;
                foreach (var waiter in _waiters.Where(waiter => waiter.Count <= count).ToList())
                {
                    waiter.Done.TrySetResult();
                    _waiters.Remove(waiter);
                }
            }
        }

        public Task WaitForAsync(int count)
        {
            Task wait;
            lock (_gate)
            {
                if (_count >= count)
                {
                    return Task.CompletedTask;
                }

                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, done));
                wait = done.Task;
            }

            return wait.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>The sources the published accounts belong to; an account without one is not published.</summary>
    private static IEnumerable<ISignalSource> Sources() =>
    [
        new FakeSource(new ProviderProfile("claude:claude-1", "Claude", "#000000")),
        new FakeSource(new ProviderProfile("antigravity", "Antigravity", "#4285F4"))
    ];

    private sealed class FakeSource(ProviderProfile profile) : ISignalSource
    {
        public ProviderProfile Profile { get; } = profile;

        public Task<ProviderReading> ReadAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }

    private static PulseState State()
    {
        var now = DateTimeOffset.UtcNow;
        var usage = new UsagePulse("claude:claude-1", now, 40, 100, 20, 100, null, null);
        var reading = new ProviderReading(
            usage,
            new IdentityCard("claude:claude-1", "Claude", null, null, "Max", "test"),
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

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class NoAccountSources : IAccountSourceRegistry
    {
        public IReadOnlyList<ISignalSource> Current => [];

        public void Reload()
        {
        }
    }

    private sealed class Orchestrator : IPulseOrchestrator
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
