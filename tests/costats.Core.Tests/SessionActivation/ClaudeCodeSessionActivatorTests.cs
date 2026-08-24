using costats.Application.SessionActivation;
using costats.Application.Settings;
using costats.Infrastructure.SessionActivation;
using Xunit;

namespace costats.Core.Tests.SessionActivation;

public sealed class ClaudeCodeSessionActivatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "costats-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Claude_invocation_is_minimal_toolless_and_uses_the_account_profile()
    {
        var activator = new ClaudeCodeSessionActivator(new AppSettings(), _root);
        var startInfo = activator.CreateStartInfo(
            new SessionActivationTarget(
                "claude:work",
                SessionActivationProvider.Claude,
                @"C:\profiles\claude-work"));

        Assert.NotNull(startInfo);
        Assert.Equal("claude", startInfo!.FileName);
        Assert.Equal(@"C:\profiles\claude-work", startInfo.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.False(startInfo.Environment.ContainsKey("ANTHROPIC_AUTH_TOKEN"));
        Assert.False(startInfo.Environment.ContainsKey("ANTHROPIC_BASE_URL"));
        AssertInvocationIsMinimal(startInfo.ArgumentList);
    }

    [Fact]
    public void Glm_invocation_uses_the_supported_coding_plan_gateway_and_air_model()
    {
        var settings = new AppSettings { ZAiCodingApiKey = "test-secret-never-sent" };
        var activator = new ClaudeCodeSessionActivator(settings, _root);
        var startInfo = activator.CreateStartInfo(
            new SessionActivationTarget("zai", SessionActivationProvider.Zai));

        Assert.NotNull(startInfo);
        Assert.Equal("test-secret-never-sent", startInfo!.Environment["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal("https://api.z.ai/api/anthropic", startInfo.Environment["ANTHROPIC_BASE_URL"]);
        Assert.Equal("glm-4.5-air", startInfo.Environment["ANTHROPIC_DEFAULT_HAIKU_MODEL"]);
        Assert.Equal("glm-4.5-air", startInfo.Environment["ANTHROPIC_SMALL_FAST_MODEL"]);
        Assert.Contains("session-activation-glm-config", startInfo.Environment["CLAUDE_CONFIG_DIR"]);
        AssertInvocationIsMinimal(startInfo.ArgumentList);
    }

    [Fact]
    public void Glm_without_a_coding_plan_key_fails_closed_before_process_start()
    {
        var activator = new ClaudeCodeSessionActivator(new AppSettings(), _root);

        var startInfo = activator.CreateStartInfo(
            new SessionActivationTarget("zai", SessionActivationProvider.Zai));

        Assert.Null(startInfo);
    }

    private static void AssertInvocationIsMinimal(System.Collections.ObjectModel.Collection<string> arguments)
    {
        Assert.Contains("Reply OK", arguments);
        AssertOption(arguments, "--model", "haiku");
        AssertOption(arguments, "--max-turns", "1");
        AssertOption(arguments, "--tools", string.Empty);
        AssertOption(arguments, "--permission-mode", "dontAsk");
        Assert.Contains("--no-session-persistence", arguments);
        AssertOption(arguments, "--output-format", "json");
    }

    private static void AssertOption(
        System.Collections.ObjectModel.Collection<string> arguments,
        string option,
        string expectedValue)
    {
        var index = arguments.IndexOf(option);
        Assert.True(index >= 0, $"Missing {option}");
        Assert.True(index + 1 < arguments.Count, $"Missing value for {option}");
        Assert.Equal(expectedValue, arguments[index + 1]);
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
}
