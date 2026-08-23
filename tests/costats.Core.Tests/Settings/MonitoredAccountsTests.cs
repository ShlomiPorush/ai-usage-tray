using costats.Application.Settings;
using Xunit;

namespace costats.Core.Tests.Settings;

public sealed class MonitoredAccountsTests
{
    [Fact]
    public void Fresh_install_defaults_to_standard_claude_and_codex_folders()
    {
        var settings = new AppSettings();

        var accounts = settings.GetEffectiveAccounts();

        Assert.Equal(2, accounts.Count);
        Assert.Contains(accounts, a => a.IsClaude && a.ConfigDir.EndsWith(".claude"));
        Assert.Contains(accounts, a => a.IsCodex && a.ConfigDir.EndsWith(".codex"));
    }

    [Fact]
    public void Legacy_settings_shape_is_migrated_to_accounts()
    {
        var settings = new AppSettings
        {
            ClaudeConfigDir = "/home/user/.claude-custom",
            OpenAiAccounts =
            [
                new OpenAiAccountSettings { Id = "openai-1", DisplayName = "PA", CodexHome = "/home/user/.codex-1" },
                new OpenAiAccountSettings { Id = "openai-2", DisplayName = "GPT", CodexHome = "/home/user/.codex-2" }
            ]
        };

        var accounts = settings.GetEffectiveAccounts();

        Assert.Equal(3, accounts.Count);
        Assert.Equal(MonitoredAccountSettings.ClaudeType, accounts[0].Type);
        Assert.Equal("/home/user/.claude-custom", accounts[0].ConfigDir);
        Assert.Equal("PA", accounts[1].DisplayName);
        Assert.Equal("GPT", accounts[2].DisplayName);
        Assert.All(accounts, a => Assert.True(a.IsValid));
    }

    [Fact]
    public void Explicit_accounts_take_precedence_over_legacy_fields()
    {
        var settings = new AppSettings
        {
            ClaudeConfigDir = "/legacy/claude",
            Accounts =
            [
                new MonitoredAccountSettings { Id = "claude-a", Type = "claude", DisplayName = "Work", ConfigDir = "/work/claude" },
                new MonitoredAccountSettings { Id = "claude-b", Type = "claude", DisplayName = "Home", ConfigDir = "/home/claude" },
                new MonitoredAccountSettings { Id = "codex-a", Type = "codex", DisplayName = "Codex", ConfigDir = "/work/codex" }
            ]
        };

        var accounts = settings.GetEffectiveAccounts();

        Assert.Equal(3, accounts.Count);
        Assert.Equal(2, accounts.Count(a => a.IsClaude));
        Assert.DoesNotContain(accounts, a => a.ConfigDir == "/legacy/claude");
    }

    [Fact]
    public void Invalid_accounts_are_filtered_out()
    {
        var settings = new AppSettings
        {
            Accounts =
            [
                new MonitoredAccountSettings { Id = "", Type = "claude", ConfigDir = "/x" },
                new MonitoredAccountSettings { Id = "a", Type = "unknown", ConfigDir = "/x" },
                new MonitoredAccountSettings { Id = "b", Type = "codex", ConfigDir = "" },
                new MonitoredAccountSettings { Id = "ok", Type = "codex", DisplayName = "OK", ConfigDir = "/ok" }
            ]
        };

        var accounts = settings.GetEffectiveAccounts();

        var account = Assert.Single(accounts);
        Assert.Equal("ok", account.Id);
    }

    [Fact]
    public void Local_usage_also_scans_standard_profiles_when_monitoring_uses_isolated_profiles()
    {
        var settings = new AppSettings
        {
            Accounts =
            [
                new MonitoredAccountSettings
                {
                    Id = "claude-isolated",
                    Type = "claude",
                    DisplayName = "Claude",
                    ConfigDir = Path.Combine("C:", "profiles", "claude-isolated")
                },
                new MonitoredAccountSettings
                {
                    Id = "codex-isolated",
                    Type = "codex",
                    DisplayName = "PA",
                    ConfigDir = Path.Combine("C:", "profiles", "codex-isolated")
                }
            ]
        };

        var accounts = settings.GetLocalUsageAccounts(Path.Combine("C:", "Users", "tester"));

        Assert.Equal(4, accounts.Count);
        Assert.Contains(accounts, account => account.ConfigDir.EndsWith(Path.Combine(".claude")));
        Assert.Contains(accounts, account => account.ConfigDir.EndsWith(Path.Combine(".codex")));
    }

    [Fact]
    public void Local_usage_does_not_duplicate_a_standard_profile_already_monitored()
    {
        var home = Path.Combine("C:", "Users", "tester");
        var settings = new AppSettings
        {
            Accounts =
            [
                new MonitoredAccountSettings
                {
                    Id = "codex-1",
                    Type = "codex",
                    DisplayName = "Codex",
                    ConfigDir = Path.Combine(home, ".codex") + Path.DirectorySeparatorChar
                }
            ]
        };

        var accounts = settings.GetLocalUsageAccounts(home);

        Assert.Equal(2, accounts.Count);
        Assert.Single(accounts, account => account.IsCodex);
        Assert.Single(accounts, account => account.IsClaude);
    }
}
