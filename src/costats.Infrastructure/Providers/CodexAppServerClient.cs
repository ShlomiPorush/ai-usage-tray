using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Reads ChatGPT-managed Codex quota windows through the official Codex app-server JSON-RPC API.
/// The client never reads or copies account tokens; Codex owns authentication and refresh.
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

    public CodexAppServerClient(string codexExecutable = "codex", TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(codexExecutable))
        {
            throw new ArgumentException("Codex executable is required.", nameof(codexExecutable));
        }

        _codexExecutable = CodexExecutableResolver.Resolve(codexExecutable);
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

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

        using var process = new Process
        {
            StartInfo = CreateStartInfo(codexHome),
            EnableRaisingEvents = true
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        CodexAppServerRateLimitSnapshot? rateLimitSnapshot = null;

        try
        {
            if (!process.Start())
            {
                return null;
            }

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

            var requiresSignIn = accountSnapshot is
                {
                    HasAccount: false,
                    RequiresOpenaiAuth: true
                } ||
                (refreshToken && accountSnapshot?.HasError == true);
            if (requiresSignIn)
            {
                return SignInRequiredSnapshot();
            }

            await process.StandardInput.WriteLineAsync(
                "{\"method\":\"account/rateLimits/read\",\"id\":2}");
            await process.StandardInput.FlushAsync(timeout.Token);

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
                    return IsAuthenticationError(error) ? SignInRequiredSnapshot() : null;
                }
                if (rateLimitSnapshot is not null)
                {
                    return rateLimitSnapshot with { Email = email };
                }
            }

            return rateLimitSnapshot;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return rateLimitSnapshot;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return rateLimitSnapshot;
        }
        finally
        {
            TryTerminate(process);
        }
    }

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
        try
        {
            if (!process.Start()) return ResetCreditOutcome.Unavailable;
            _ = DrainAsync(process.StandardError, timeout.Token);
            return await ExchangeResetCreditAsync(process.StandardOutput, process.StandardInput,
                creditId, idempotencyKey, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Losing the response does not prove that the provider refused the
            // reset. The service keeps the same key for a subsequent retry.
            return ResetCreditOutcome.Unknown;
        }
        finally { TryTerminate(process); }
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
            FileName = _codexExecutable,
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

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
        }
        catch
        {
            // Process cleanup must not turn a failed refresh into an app crash.
        }
    }

    public void Dispose()
    {
    }
}
