using costats.Core.Tray;
using Xunit;

namespace costats.Core.Tests.Tray;

public sealed class TrayStatusComposerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Compose_uses_highest_used_percentage_across_every_window()
    {
        var accounts = new[]
        {
            new AccountUsageStatus("Claude", 34, Now.AddHours(2).AddMinutes(34), 60, Now.AddDays(3.2)),
            new AccountUsageStatus("OpenAI 1", 82, Now.AddHours(4), 45, Now.AddDays(5)),
            new AccountUsageStatus("OpenAI 2", 27, Now.AddHours(1), 73, Now.AddDays(2.6))
        };

        var result = TrayStatusComposer.Compose(accounts, Now);

        Assert.Equal(73, result.HighestUsedPercent);
        Assert.Equal(TraySeverity.Amber, result.Severity);
    }

    [Fact]
    public void Compose_formats_all_accounts_in_one_compact_hover_tooltip()
    {
        var accounts = new[]
        {
            new AccountUsageStatus("Claude", 34, Now.AddHours(2).AddMinutes(34), 60, Now.AddDays(3.2)),
            new AccountUsageStatus("PA", null, null, 45, Now.AddDays(5.1)),
            new AccountUsageStatus("GPT", null, null, 73, Now.AddDays(2.6))
        };

        var result = TrayStatusComposer.Compose(accounts, Now);

        Assert.Equal(
            "Claude Session 66% · 2h34m | Weekly 40% · 3.2d\n" +
            "PA Weekly 55% · 5.1d\n" +
            "GPT Weekly 27% · 2.6d",
            result.Tooltip);
        Assert.True(result.Tooltip.Length <= 127);
    }

    [Fact]
    public void Compose_omits_a_window_that_does_not_exist_for_the_account()
    {
        var accounts = new[]
        {
            new AccountUsageStatus("PA", null, null, 87, Now.AddDays(6.8))
        };

        var result = TrayStatusComposer.Compose(accounts, Now);

        Assert.Equal("PA Weekly 13% · 6.8d", result.Tooltip);
    }

    [Fact]
    public void FromUsagePulse_converts_used_percentages_to_remaining_percentages()
    {
        var usage = new costats.Core.Pulse.UsagePulse(
            "claude", Now, 66, 100, 40, 100, null, null,
            new costats.Core.Pulse.QuotaWindow(TimeSpan.FromHours(5), Now.AddHours(2)),
            new costats.Core.Pulse.QuotaWindow(TimeSpan.FromDays(7), Now.AddDays(3)));

        var status = AccountUsageStatus.FromUsagePulse("Claude", usage);

        Assert.Equal(34, status.SessionRemainingPercent);
        Assert.Equal(60, status.WeeklyRemainingPercent);
        Assert.Equal(Now.AddHours(2), status.SessionResetsAt);
        Assert.Equal(Now.AddDays(3), status.WeeklyResetsAt);
    }

    [Theory]
    [InlineData(51, TraySeverity.Green)]
    [InlineData(50, TraySeverity.Amber)]
    [InlineData(20, TraySeverity.Amber)]
    [InlineData(19, TraySeverity.Red)]
    public void Compose_maps_highest_used_to_expected_colour(double remaining, TraySeverity expected)
    {
        var accounts = new[]
        {
            new AccountUsageStatus("Claude", remaining, Now.AddHours(1), null, null)
        };

        Assert.Equal(expected, TrayStatusComposer.Compose(accounts, Now).Severity);
    }

    [Fact]
    public void Scoped_limits_drive_severity_and_highest_used()
    {
        var accounts = new[]
        {
            new AccountUsageStatus(
                "Claude",
                SessionRemainingPercent: 93,
                SessionResetsAt: DateTimeOffset.UtcNow.AddHours(2),
                WeeklyRemainingPercent: 60,
                WeeklyResetsAt: DateTimeOffset.UtcNow.AddDays(3),
                ScopedQuotas: [new costats.Core.Pulse.ScopedQuota("Fable", "weekly", 100, null, true)])
        };

        var status = TrayStatusComposer.Compose(accounts, DateTimeOffset.UtcNow);

        Assert.Equal(100, status.HighestUsedPercent);
        Assert.Equal(TraySeverity.Red, status.Severity);
    }
}
