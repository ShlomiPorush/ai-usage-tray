using System.Text.Json;
using costats.Application.Settings;
using Xunit;

namespace costats.Core.Tests.Settings;

public sealed class OpenAiAccountSettingsTests
{
    [Theory]
    [InlineData(" PA ", "OpenAI 1", "PA")]
    [InlineData("", "OpenAI 1", "OpenAI 1")]
    [InlineData("   ", "GPT", "GPT")]
    public void NormalizeDisplayName_trims_and_falls_back_for_blank_names(
        string value,
        string fallback,
        string expected)
    {
        Assert.Equal(expected, OpenAiAccountSettings.NormalizeDisplayName(value, fallback));
    }

    [Fact]
    public void Claude_subscription_profile_uses_an_isolated_config_directory()
    {
        var settings = new AppSettings();

        Assert.EndsWith(".claude-ai-usage-tray", settings.ClaudeConfigDir, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.Combine(".claude", ".credentials.json"), settings.ClaudeConfigDir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Custom_names_round_trip_without_changing_account_identity_or_home()
    {
        var settings = new AppSettings();
        var firstHome = settings.OpenAiAccounts[0].CodexHome;
        var secondHome = settings.OpenAiAccounts[1].CodexHome;
        settings.OpenAiAccounts[0].DisplayName = "PA";
        settings.OpenAiAccounts[1].DisplayName = "GPT";

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal("PA", restored.OpenAiAccounts[0].DisplayName);
        Assert.Equal("GPT", restored.OpenAiAccounts[1].DisplayName);
        Assert.Equal("openai-1", restored.OpenAiAccounts[0].Id);
        Assert.Equal("openai-2", restored.OpenAiAccounts[1].Id);
        Assert.Equal(firstHome, restored.OpenAiAccounts[0].CodexHome);
        Assert.Equal(secondHome, restored.OpenAiAccounts[1].CodexHome);
    }

    [Fact]
    public void NormalizeDisplayName_limits_names_to_24_characters()
    {
        var result = OpenAiAccountSettings.NormalizeDisplayName(new string('A', 30), "OpenAI");

        Assert.Equal(24, result.Length);
    }
}
