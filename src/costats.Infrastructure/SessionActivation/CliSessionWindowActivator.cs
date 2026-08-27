using System.Diagnostics;
using costats.Application.SessionActivation;
using costats.Application.Settings;
using costats.Infrastructure.Providers;

namespace costats.Infrastructure.SessionActivation;

/// <summary>
/// Sends a minimal activation prompt through the official CLI that owns each
/// provider login. Each invocation is isolated from user customizations.
/// </summary>
public sealed class CliSessionWindowActivator : ISessionWindowActivator
{
    private const string Prompt = "Reply OK";
    private const string ZaiBaseUrl = "https://api.z.ai/api/anthropic";
    private const string ZaiHaikuModel = "glm-4.5-air";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private readonly AppSettings _settings;
    private readonly string _workingDirectory;
    private readonly string _zaiConfigDirectory;

    public CliSessionWindowActivator(AppSettings settings, string? basePath = null)
    {
        _settings = settings;
        var root = string.IsNullOrWhiteSpace(basePath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : basePath;
        _workingDirectory = Path.Combine(root, "costats", "session-activation-work");
        _zaiConfigDirectory = Path.Combine(root, "costats", "session-activation-glm-config");
    }

    public async Task<SessionActivationResult> ActivateAsync(
        SessionActivationTarget target,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_workingDirectory);

            var startInfo = CreateStartInfo(target);
            if (startInfo is null)
            {
                return SessionActivationResult.Failure("The required provider credential is unavailable");
            }

            using var process = new Process { StartInfo = startInfo };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            try
            {
                process.Start();
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return SessionActivationResult.Failure($"{DescribeCli(target)} is not installed or could not be started");
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeout.Token);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return SessionActivationResult.Failure($"{DescribeCli(target)} timed out");
            }

            if (process.ExitCode == 0)
            {
                return SessionActivationResult.Success();
            }

            // Do not return CLI stderr: authentication errors can include
            // endpoint or account details. The full process output is discarded.
            return SessionActivationResult.Failure($"{DescribeCli(target)} exited with code {process.ExitCode}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SessionActivationResult.Failure("Could not prepare the activation process");
        }
    }

    internal ProcessStartInfo? CreateStartInfo(SessionActivationTarget target) =>
        target.Provider == SessionActivationProvider.Codex
            ? CreateCodexStartInfo(target)
            : CreateClaudeStartInfo(target);

    private ProcessStartInfo? CreateClaudeStartInfo(SessionActivationTarget target)
    {
        var startInfo = CreateBaseStartInfo("claude");

        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(Prompt);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add("haiku");
        startInfo.ArgumentList.Add("--max-turns");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--tools");
        startInfo.ArgumentList.Add(string.Empty);
        startInfo.ArgumentList.Add("--safe-mode");
        startInfo.ArgumentList.Add("--permission-mode");
        startInfo.ArgumentList.Add("dontAsk");
        startInfo.ArgumentList.Add("--no-session-persistence");
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("json");

        // An activation check must never update the CLI or send optional
        // telemetry/config traffic just because a reset boundary was reached.
        startInfo.Environment["DISABLE_AUTOUPDATER"] = "1";
        startInfo.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";

        if (target.Provider == SessionActivationProvider.Claude)
        {
            if (string.IsNullOrWhiteSpace(target.ConfigDirectory))
            {
                return null;
            }

            // The monitored Claude profile owns its OAuth login. Do not let a
            // machine-wide third-party gateway variable silently redirect this
            // activation to another provider.
            startInfo.Environment.Remove("ANTHROPIC_AUTH_TOKEN");
            startInfo.Environment.Remove("ANTHROPIC_API_KEY");
            startInfo.Environment.Remove("ANTHROPIC_BASE_URL");
            startInfo.Environment["CLAUDE_CONFIG_DIR"] = target.ConfigDirectory;
            return startInfo;
        }

        var key = _settings.ZAiCodingApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        Directory.CreateDirectory(_zaiConfigDirectory);
        startInfo.Environment["CLAUDE_CONFIG_DIR"] = _zaiConfigDirectory;
        startInfo.Environment["ANTHROPIC_AUTH_TOKEN"] = key.Trim();
        startInfo.Environment["ANTHROPIC_BASE_URL"] = ZaiBaseUrl;
        startInfo.Environment["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = ZaiHaikuModel;
        startInfo.Environment["ANTHROPIC_SMALL_FAST_MODEL"] = ZaiHaikuModel;
        startInfo.Environment.Remove("ANTHROPIC_API_KEY");
        startInfo.Environment.Remove("CLAUDE_CODE_USE_BEDROCK");
        startInfo.Environment.Remove("CLAUDE_CODE_USE_VERTEX");
        return startInfo;
    }

    private ProcessStartInfo? CreateCodexStartInfo(SessionActivationTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.ConfigDirectory))
        {
            return null;
        }

        var startInfo = CreateBaseStartInfo(CodexExecutableResolver.Resolve("codex"));
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--ephemeral");
        startInfo.ArgumentList.Add("--ignore-user-config");
        startInfo.ArgumentList.Add("--ignore-rules");
        startInfo.ArgumentList.Add("--sandbox");
        startInfo.ArgumentList.Add("read-only");
        startInfo.ArgumentList.Add("--skip-git-repo-check");
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add("approval_policy=\"never\"");
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add("web_search=\"disabled\"");
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add("features.shell_tool=false");
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add("shell_environment_policy.inherit=\"none\"");
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add("check_for_update_on_startup=false");
        startInfo.ArgumentList.Add("--color");
        startInfo.ArgumentList.Add("never");
        startInfo.ArgumentList.Add("--json");
        startInfo.ArgumentList.Add(Prompt);

        // Authentication still comes from the monitored CODEX_HOME, while the
        // explicit CLI policy keeps this background run isolated from account
        // customizations, external tools, inherited credentials and update checks.
        startInfo.Environment.Remove("OPENAI_API_KEY");
        startInfo.Environment.Remove("OPENAI_BASE_URL");
        startInfo.Environment.Remove("OPENAI_ORG_ID");
        startInfo.Environment.Remove("OPENAI_ORGANIZATION");
        startInfo.Environment.Remove("CODEX_API_KEY");
        startInfo.Environment["CODEX_HOME"] = target.ConfigDirectory;
        return startInfo;
    }

    private ProcessStartInfo CreateBaseStartInfo(string executable) => new()
    {
        FileName = executable,
        WorkingDirectory = _workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    private static string DescribeCli(SessionActivationTarget target) =>
        target.Provider == SessionActivationProvider.Codex ? "Codex CLI" : "Claude Code";

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Timeout cleanup only.
        }
    }
}
