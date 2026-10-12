using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using costats.Application.Diagnostics;
using System.Globalization;
using costats.Core.Shell;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Reads ChatGPT-managed Codex quota windows through the official Codex app-server JSON-RPC API.
/// The client never reads or copies account tokens; Codex owns authentication and refresh.
/// Logging follows the same rule: it records the JSON-RPC method names, how
/// each answer was classified and process lifetimes, never a raw response line,
/// the child's environment or anything Codex prints.
/// </summary>
public interface ICodexAppServerClient
{
    Task<CodexAppServerRateLimitSnapshot?> FetchAsync(
        string codexHome,
        bool refreshToken,
        CancellationToken cancellationToken);
}

public sealed class CodexAppServerClient : ICodexAppServerClient, ICodexResetCreditClient, IDisposable
{
    private readonly string _codexExecutable;
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;

    /// <summary>
    /// The failure last reported at Warning per account folder. A failing
    /// account is reported once per distinct kind of failure, not on every
    /// refresh, and once more when it works again; Debug keeps every detail.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _reportedFailures = new(StringComparer.OrdinalIgnoreCase);

    public CodexAppServerClient(
        string codexExecutable = "codex",
        TimeSpan? timeout = null,
        ILogger<CodexAppServerClient>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(codexExecutable))
        {
            throw new ArgumentException("Codex executable is required.", nameof(codexExecutable));
        }

        // Resolved again for every start, so installing or moving Codex while
        // the app runs needs no restart.
        _codexExecutable = codexExecutable;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// The account folder's name, for logs. Not the full path, which carries
    /// the Windows user name, and never anything from inside the folder.
    /// </summary>
    internal static string AccountLabel(string codexHome) =>
        Path.GetFileName(codexHome.TrimEnd('\\', '/'));

    public async Task<CodexAppServerRateLimitSnapshot?> FetchAsync(
        string codexHome,
        bool refreshToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            throw new ArgumentException("A separate CODEX_HOME is required for each account.", nameof(codexHome));
        }

        Directory.CreateDirectory(codexHome);
        var account = AccountLabel(codexHome);

        using var process = new Process
        {
            StartInfo = CreateStartInfo(codexHome),
            EnableRaisingEvents = true
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        CodexAppServerRateLimitSnapshot? rateLimitSnapshot = null;
        var stopwatch = Stopwatch.StartNew();
        var started = false;

        try
        {
            if (!process.Start())
            {
                _logger.LogDebug("Codex app-server for {Account} did not start", account);
                ReportFailure(codexHome, account, "the app-server did not start", null);
                return null;
            }

            started = true;
            _logger.LogDebug(
                "Codex app-server started for {Account} from {Executable} (PID {Pid}, refreshToken {RefreshToken})",
                account,
                ShortenProfilePath(process.StartInfo.FileName),
                process.Id,
                refreshToken);

            // codex app-server writes progress to stderr. Leaving that pipe
            // unread deadlocks the child once its buffer fills, which stalls the
            // stdout loop below until the timeout fires.
            _ = DrainAsync(process.StandardError, timeout.Token);

            await process.StandardInput.WriteLineAsync(
                "{\"method\":\"initialize\",\"id\":1,\"params\":{\"clientInfo\":{\"name\":\"ai_usage_tray\",\"title\":\"AI Usage Tray\",\"version\":\"0.1.0\"}}}");
            await process.StandardInput.WriteLineAsync(
                "{\"method\":\"initialized\",\"params\":{}}");
            await process.StandardInput.WriteLineAsync(
                CreateAccountReadRequest(refreshToken));
            await process.StandardInput.FlushAsync(timeout.Token);
            _logger.LogDebug(
                "Codex app-server {Account}: sent initialize, initialized, account/read (refreshToken {RefreshToken})",
                account,
                refreshToken);

            CodexAppServerAccountSnapshot? accountSnapshot = null;
            while (!timeout.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    break;
                }

                if (CodexAppServerRateLimitParser.TryParseAccount(line, expectedId: 3, out accountSnapshot))
                {
                    break;
                }
            }

            _logger.LogDebug(
                "Codex app-server {Account}: account/read answered {Result} after {ElapsedMs} ms",
                account,
                DescribeAccountRead(accountSnapshot),
                stopwatch.ElapsedMilliseconds);
            if (accountSnapshot is null)
            {
                ReportFailure(codexHome, account, "the app-server gave no account answer", null);
            }
            else
            {
                ReportSuccess(codexHome, account);
            }

            var requiresSignIn = accountSnapshot is
                {
                    HasAccount: false,
                    RequiresOpenaiAuth: true
                } ||
                (refreshToken && accountSnapshot?.HasError == true);
            if (requiresSignIn)
            {
                _logger.LogDebug("Codex app-server {Account}: sign-in required", account);
                return SignInRequiredSnapshot();
            }

            await process.StandardInput.WriteLineAsync(
                "{\"method\":\"account/rateLimits/read\",\"id\":2}");
            await process.StandardInput.FlushAsync(timeout.Token);
            _logger.LogDebug("Codex app-server {Account}: sent account/rateLimits/read", account);

            var email = accountSnapshot?.Email;
            while (!timeout.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    break;
                }

                rateLimitSnapshot ??= CodexAppServerRateLimitParser.Parse(line, expectedId: 2);
                if (CodexAppServerRateLimitParser.TryParseError(line, expectedId: 2, out var error))
                {
                    var isAuthentication = IsAuthenticationError(error);
                    _logger.LogDebug(
                        "Codex app-server {Account}: account/rateLimits/read answered an {ErrorKind} error after {ElapsedMs} ms: {Error}",
                        account,
                        isAuthentication ? "authentication" : "other",
                        stopwatch.ElapsedMilliseconds,
                        LogRedaction.Redact(error));
                    return isAuthentication ? SignInRequiredSnapshot() : null;
                }
                if (rateLimitSnapshot is not null)
                {
                    _logger.LogDebug(
                        "Codex app-server {Account}: account/rateLimits/read answered rate limits after {ElapsedMs} ms",
                        account,
                        stopwatch.ElapsedMilliseconds);
                    return rateLimitSnapshot with { Email = email };
                }
            }

            _logger.LogDebug(
                "Codex app-server {Account}: output ended after {ElapsedMs} ms without rate limits",
                account,
                stopwatch.ElapsedMilliseconds);
            return rateLimitSnapshot;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Codex app-server for {Account} did not answer within {TimeoutSeconds} s",
                account,
                _timeout.TotalSeconds);
            ReportFailure(
                codexHome,
                account,
                string.Create(CultureInfo.InvariantCulture, $"no answer within {_timeout.TotalSeconds} s"),
                null);
            return rateLimitSnapshot;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Codex not installed (Win32Exception), a broken pipe when the
            // child exits early, or similar. The refresh shows "no data"; the
            // reason belongs in the normal log.
            var stage = started ? "while talking to the app-server" : "starting the app-server";
            _logger.LogDebug(ex, "Codex app-server read for {Account} failed ({Stage})", account, stage);
            ReportFailure(codexHome, account, $"{stage}: {ex.GetType().Name}", ex);
            return rateLimitSnapshot;
        }
        finally
        {
            TryTerminate(process, account, stopwatch, _logger);
        }
    }

    /// <summary>
    /// Warns once per account and failure kind. The same failure on the next
    /// refresh stays at Debug (logged by the caller with full detail).
    /// </summary>
    private void ReportFailure(string codexHome, string account, string kind, Exception? exception)
    {
        var key = Path.GetFullPath(codexHome);
        if (_reportedFailures.TryGetValue(key, out var previous) &&
            string.Equals(previous, kind, StringComparison.Ordinal))
        {
            return;
        }

        _reportedFailures[key] = kind;
        _logger.LogWarning(
            "Codex app-server for {Account} is not working ({Failure}){Detail}; repeats are logged at Debug until it works again",
            account,
            kind,
            exception is null ? string.Empty : ": " + LogRedaction.Redact(exception.Message));
    }

    private void ReportSuccess(string codexHome, string account)
    {
        if (_reportedFailures.TryRemove(Path.GetFullPath(codexHome), out var previous))
        {
            _logger.LogInformation("Codex app-server for {Account} works again (was: {Failure})", account, previous);
        }
    }

    /// <summary>The executable path for logs, with the user profile folder shown as %USERPROFILE%.</summary>
    internal static string ShortenProfilePath(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(profile) && path.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? "%USERPROFILE%" + path[profile.Length..]
            : path;
    }

    private static string DescribeAccountRead(CodexAppServerAccountSnapshot? snapshot) => snapshot switch
    {
        null => "nothing (output ended or timed out)",
        { HasError: true } => "an error",
        { HasAccount: true } => "a signed-in account",
        { RequiresOpenaiAuth: true } => "no account, OpenAI sign-in required",
        _ => "no account"
    };

    internal static string CreateAccountReadRequest(bool refreshToken) =>
        $"{{\"method\":\"account/read\",\"id\":3,\"params\":{{\"refreshToken\":{refreshToken.ToString().ToLowerInvariant()}}}}}";

    public async Task<ResetCreditOutcome> ConsumeResetCreditAsync(
        string codexHome, string creditId, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        ArgumentException.ThrowIfNullOrWhiteSpace(creditId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        using var process = new Process { StartInfo = CreateStartInfo(codexHome) };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var account = AccountLabel(codexHome);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!process.Start()) return ResetCreditOutcome.Unavailable;
            _logger.LogDebug("Codex app-server started for {Account} to redeem a reset (PID {Pid})", account, process.Id);
            _ = DrainAsync(process.StandardError, timeout.Token);
            var outcome = await ExchangeResetCreditAsync(process.StandardOutput, process.StandardInput,
                creditId, idempotencyKey, timeout.Token).ConfigureAwait(false);
            _logger.LogDebug(
                "Codex app-server {Account}: account/rateLimitResetCredit/consume answered {Outcome} after {ElapsedMs} ms",
                account,
                outcome,
                stopwatch.ElapsedMilliseconds);
            return outcome;
        }
        catch (Exception ex)
        {
            // Losing the response does not prove that the provider refused the
            // reset. The service keeps the same key for a subsequent retry.
            _logger.LogWarning(
                ex,
                "Codex reset redemption for {Account} lost its answer: {ErrorKind}; the outcome is unknown",
                account,
                ex.GetType().Name);
            return ResetCreditOutcome.Unknown;
        }
        finally { TryTerminate(process, account, stopwatch, _logger); }
    }

    internal static async Task<ResetCreditOutcome> ExchangeResetCreditAsync(
        TextReader output, TextWriter input, string creditId, string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(creditId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        await input.WriteLineAsync("{\"method\":\"initialize\",\"id\":1,\"params\":{\"clientInfo\":{\"name\":\"ai_usage_tray\",\"version\":\"0.1.0\"}}}");
        await input.FlushAsync(cancellationToken);
        var initialized = false;
        while (await output.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var responseId)) continue;
            if (responseId != (initialized ? 4 : 1)) continue;
            if (root.TryGetProperty("error", out var error))
            {
                return error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) &&
                    code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var errorCode) && errorCode == -32601
                    ? ResetCreditOutcome.Unsupported : ResetCreditOutcome.Unknown;
            }
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                return ResetCreditOutcome.Unknown;
            if (!initialized)
            {
                // Older servers can accept consume while ignoring an unknown creditId.
                // Only send a redemption to a version verified to support explicit selection.
                if (!SupportsResetCreditSelection(result)) return ResetCreditOutcome.Unsupported;
                await input.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}");
                await input.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    method = "account/rateLimitResetCredit/consume", id = 4,
                    @params = new { creditId, idempotencyKey }
                }));
                await input.FlushAsync(cancellationToken);
                initialized = true;
                continue;
            }
            if (!result.TryGetProperty("outcome", out var outcome) || outcome.ValueKind != JsonValueKind.String)
                return ResetCreditOutcome.Unknown;
            return outcome.GetString() switch
            {
                "reset" => ResetCreditOutcome.Reset,
                "alreadyRedeemed" => ResetCreditOutcome.AlreadyRedeemed,
                "nothingToReset" => ResetCreditOutcome.NothingToReset,
                "noCredit" => ResetCreditOutcome.NoCredit,
                _ => ResetCreditOutcome.Unknown
            };
        }
        return ResetCreditOutcome.Unknown;
    }

    private static bool SupportsResetCreditSelection(JsonElement initialization)
    {
        if (!initialization.TryGetProperty("userAgent", out var agent) || agent.ValueKind != JsonValueKind.String)
            return false;
        var match = Regex.Match(agent.GetString() ?? string.Empty,
            @"^[^/\s]+/(\d+\.\d+\.\d+)(?:\s|$)", RegexOptions.CultureInvariant);
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version) &&
            version >= new Version(0, 154, 0);
    }

    private static CodexAppServerRateLimitSnapshot SignInRequiredSnapshot() =>
        new(null, null, null, null, null, null)
        {
            RequiresSignIn = true
        };

    internal static bool IsAuthenticationError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        string[] markers =
        [
            "401",
            "auth",
            "login",
            "sign in",
            "sign-in",
            "token",
            "unauthorized"
        ];
        return markers.Any(marker => error.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads a redirected stream to completion, ignoring failures. Never throws,
    /// so callers can leave it running without observing the task.
    /// </summary>
    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Draining exists only to keep the child's pipe from filling up.
        }
    }

    private ProcessStartInfo CreateStartInfo(string codexHome)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = CodexExecutableResolver.Resolve(_codexExecutable),
            // Pin a trusted rooted working directory so the child does not
            // inherit or search an attacker-writable current directory.
            WorkingDirectory = SystemExecutables.TrustedWorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.Environment["CODEX_HOME"] = Path.GetFullPath(codexHome);
        return startInfo;
    }

    private static void TryTerminate(Process process, string account, Stopwatch stopwatch, ILogger logger)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
                logger.LogDebug(
                    "Codex app-server for {Account} stopped by the app after {ElapsedMs} ms",
                    account,
                    stopwatch.ElapsedMilliseconds);
            }
            else
            {
                logger.LogDebug(
                    "Codex app-server for {Account} exited with code {ExitCode} after {ElapsedMs} ms",
                    account,
                    process.ExitCode,
                    stopwatch.ElapsedMilliseconds);
            }
        }
        catch (InvalidOperationException)
        {
            // The process never started, so there is nothing to stop.
        }
        catch (Exception ex)
        {
            // Process cleanup must not turn a failed refresh into an app crash.
            logger.LogDebug("Stopping the Codex app-server for {Account} failed: {ErrorKind}: {Error}", account, ex.GetType().Name, LogRedaction.Redact(ex.Message));
        }
    }

    public void Dispose()
    {
    }
}
