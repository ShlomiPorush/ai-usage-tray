using costats.Application.Settings;
using Xunit;

namespace costats.Core.Tests.Settings;

public sealed class UsageAlertSettingsTests
{
    [Fact]
    public void Missing_account_rules_are_disabled_with_the_default_threshold()
    {
        var settings = new AppSettings();

        Assert.False(settings.UsageAlertsEnabled);
        Assert.False(settings.UsageResetAlertsEnabled);
        Assert.False(settings.IsUsageAlertProviderEnabled("claude:work"));
        Assert.Equal(90, settings.GetUsageAlertThreshold("claude:work"));
    }

    [Fact]
    public void Account_rules_are_independent_and_thresholds_are_clamped()
    {
        var settings = new AppSettings();

        settings.SetUsageAlertRule("claude:work", enabled: true, thresholdPercent: 80);
        settings.SetUsageAlertRule("codex:personal", enabled: false, thresholdPercent: 250);

        Assert.True(settings.IsUsageAlertProviderEnabled("CLAUDE:WORK"));
        Assert.Equal(80, settings.GetUsageAlertThreshold("claude:work"));
        Assert.False(settings.IsUsageAlertProviderEnabled("codex:personal"));
        Assert.Equal(100, settings.GetUsageAlertThreshold("codex:personal"));
    }

    [Fact]
    public void Deserialized_rules_are_normalized_and_deduplicated()
    {
        var settings = new AppSettings
        {
            UsageAlertRules =
            [
                new UsageAlertRuleSettings { ProviderId = " claude:work ", Enabled = false, ThresholdPercent = 70 },
                new UsageAlertRuleSettings { ProviderId = "CLAUDE:WORK", Enabled = true, ThresholdPercent = 85 },
                new UsageAlertRuleSettings { ProviderId = "", Enabled = true, ThresholdPercent = 50 }
            ]
        };

        var rule = Assert.Single(settings.UsageAlertRules);
        Assert.Equal("claude:work", rule.ProviderId, ignoreCase: true);
        Assert.True(rule.Enabled);
        Assert.Equal(85, rule.ThresholdPercent);
    }
}
