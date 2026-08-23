using System.Diagnostics;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Reads ChatGPT-managed Codex quota windows through the official Codex app-server JSON-RPC API.
/// The client never reads or copies account tokens; Codex owns authentication and refresh.
/// </summary>
public interface ICodexAppServerClient
{
    Task<CodexAppServerRateLimitSnapshot?> FetchAsync(string codexHome, CancellationToken cancellationToken);
}

public sealed class CodexAppServerClient : ICodexAppServerClient, IDisposable
{
    private readonly string _codexExecutable;
    private readonly TimeSpan _timeout;

    public CodexAppServerClient(string codexExecutable = "codex", TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(codexExecutable))
        {
            throw new ArgumentException("Codex executable is required.", nameof(codexExecutable));
        }

        _codexExecutable = ResolveExecutable(codexExecutable);
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    private static string ResolveExecutable(string executable)
    {
        if (!executable.Equals("codex", StringComparison.OrdinalIgnoreCase) || !OperatingSystem.IsWindows())
        {
            return executable;
        }

        var standaloneInstallerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "OpenAI",
            "Codex",
            "bin",
            "codex.exe");

        return File.Exists(standaloneInstallerPath) ? standaloneInstallerPath : executable;
    }

    public async Task<CodexAppServerRateLimitSnapshot?> FetchAsync(
        string codexHome,
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
                "{\"method\":\"account/rateLimits/read\",\"id\":2}");
            await process.StandardInput.WriteLineAsync(
                "{\"method\":\"account/read\",\"id\":3,\"params\":{\"refreshToken\":false}}");
            await process.StandardInput.FlushAsync(timeout.Token);

            var accountReadCompleted = false;
            string? email = null;
            while (!timeout.IsCancellationRequested && !process.HasExited)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    break;
                }

                rateLimitSnapshot ??= CodexAppServerRateLimitParser.Parse(line, expectedId: 2);
                if (!accountReadCompleted &&
                    CodexAppServerRateLimitParser.TryParseAccountEmail(line, expectedId: 3, out var parsedEmail))
                {
                    accountReadCompleted = true;
                    email = parsedEmail;
                }

                if (rateLimitSnapshot is not null && accountReadCompleted)
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
