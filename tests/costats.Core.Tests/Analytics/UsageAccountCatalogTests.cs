using costats.Application.Analytics;
using costats.Core.Analytics;
using costats.Core.Pulse;
using Xunit;

namespace costats.Core.Tests.Analytics;

public sealed class UsageAccountCatalogTests
{
    [Fact]
    public void Build_keeps_each_monitored_account_and_maps_only_real_log_buckets()
    {
        var profiles = new[]
        {
            new ProviderProfile("claude:claude-1", "Claude", "#f00"),
            new ProviderProfile("codex:openai-1", "PA", "#0f0"),
            new ProviderProfile("codex:openai-2", "GPT", "#0f0"),
            new ProviderProfile("zai", "GLM", "#00f")
        };
        var analytics = new[]
        {
            new UsageAccountInfo("claude-1", "Claude", UsageProviderKind.Claude),
            new UsageAccountInfo(UsageAccounts.MergedCodexId, UsageAccounts.MergedCodexDisplayName, UsageProviderKind.Codex)
        };

        var result = UsageAccountCatalog.Build(profiles, analytics);

        Assert.Equal(["Claude", "GPT", "PA", "GLM"], result.Select(account => account.DisplayName));
        Assert.Equal(["claude-1"], result.Single(account => account.DisplayName == "Claude").AnalyticsAccountIds);
        Assert.Equal([UsageAccounts.MergedCodexId], result.Single(account => account.DisplayName == "PA").AnalyticsAccountIds);
        Assert.Equal([UsageAccounts.MergedCodexId], result.Single(account => account.DisplayName == "GPT").AnalyticsAccountIds);
        Assert.Empty(result.Single(account => account.DisplayName == "GLM").AnalyticsAccountIds);
    }

    [Fact]
    public void Build_assigns_standard_claude_logs_to_the_only_monitored_claude_account()
    {
        var profiles = new[]
        {
            new ProviderProfile("claude:claude-1", "Claude", "#f00")
        };
        var analytics = new[]
        {
            new UsageAccountInfo("claude-1", "Claude", UsageProviderKind.Claude),
            new UsageAccountInfo("claude-default", "Claude (default)", UsageProviderKind.Claude)
        };

        var account = Assert.Single(UsageAccountCatalog.Build(profiles, analytics));

        Assert.Equal(["claude-1", "claude-default"], account.AnalyticsAccountIds);
    }

    [Fact]
    public void Build_does_not_merge_standard_logs_when_multiple_claude_accounts_are_monitored()
    {
        var profiles = new[]
        {
            new ProviderProfile("claude:claude-1", "Work", "#f00"),
            new ProviderProfile("claude:claude-2", "Personal", "#f00")
        };
        var analytics = new[]
        {
            new UsageAccountInfo("claude-1", "Work", UsageProviderKind.Claude),
            new UsageAccountInfo("claude-2", "Personal", UsageProviderKind.Claude),
            new UsageAccountInfo("claude-default", "Claude (default)", UsageProviderKind.Claude)
        };

        var result = UsageAccountCatalog.Build(profiles, analytics);

        Assert.Equal(["claude-1"], result.Single(account => account.DisplayName == "Work").AnalyticsAccountIds);
        Assert.Equal(["claude-2"], result.Single(account => account.DisplayName == "Personal").AnalyticsAccountIds);
    }
}
