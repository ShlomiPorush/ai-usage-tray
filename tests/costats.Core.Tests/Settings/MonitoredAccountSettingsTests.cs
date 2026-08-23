using System.Text.Json;
using costats.Application.Settings;
using Xunit;

namespace costats.Core.Tests.Settings;

public sealed class MonitoredAccountSettingsTests
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
        Assert.Equal(expected, MonitoredAccountSettings.NormalizeDisplayName(value, fallback));
    }

    [Fact]
    public void Accounts_round_trip_through_json_without_losing_identity()
    {
        var settings = new AppSettings
        {
            Accounts =
            [
                new MonitoredAccountSettings { Id = "claude-1", Type = "claude", DisplayName = "Claude", ConfigDir = "/home/u/.claude" },
                new MonitoredAccountSettings { Id = "codex-1", Type = "codex", DisplayName = "PA", ConfigDir = "/home/u/.codex" },
                new MonitoredAccountSettings { Id = "codex-2", Type = "codex", DisplayName = "GPT", ConfigDir = "/home/u/.codex-2" }
            ]
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<AppSettings>(json)!;
        var accounts = restored.GetEffectiveAccounts();

        Assert.Equal(3, accounts.Count);
        Assert.Equal("claude-1", accounts[0].Id);
        Assert.Equal("PA", accounts[1].DisplayName);
        Assert.Equal("/home/u/.codex-2", accounts[2].ConfigDir);
    }

    [Fact]
    public void NormalizeDisplayName_limits_names_to_24_characters()
    {
        var result = MonitoredAccountSettings.NormalizeDisplayName(new string('A', 30), "OpenAI");

        Assert.Equal(24, result.Length);
    }
}
