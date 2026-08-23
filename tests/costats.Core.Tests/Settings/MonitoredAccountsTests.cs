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
}
