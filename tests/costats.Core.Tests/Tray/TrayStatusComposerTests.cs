using costats.Core.Tray;
using Xunit;

namespace costats.Core.Tests.Tray;

public sealed class TrayStatusComposerTests
{
    [Fact]
    public void Compose_builds_the_reference_two_row_clock_panel_text_with_used_percentages()
    {
        var now = DateTimeOffset.Parse("2026-08-23T12:00:00Z");
        var accounts = new[]
        {
            new AccountUsageStatus("Claude", 67, now.AddHours(1).AddMinutes(5), 94, now.AddDays(6)),
            new AccountUsageStatus("GLM", null, null, null, null),
            new AccountUsageStatus("GPT", null, null, 83, now.AddDays(3.6)),
            new AccountUsageStatus("PA", null, null, 66, now.AddDays(3.6))
        };

        var status = TrayStatusComposer.Compose(accounts, now);

        Assert.Equal(
            "PA: W 34%-3.6d | Claude: W 6%-6.0d <> S 33%-1h05m\n" +
            "GPT: W 17%-3.6d | GLM: unavailable",
            status.PanelText);
    }

    [Fact]
    public void Compose_can_show_percent_left_without_changing_risk_severity()
    {
        var now = DateTimeOffset.Parse("2026-08-23T12:00:00Z");
        var account = new AccountUsageStatus(
            "PA", 67, now.AddHours(1), 94, now.AddDays(6));

        var status = TrayStatusComposer.Compose([account], now, showPercentageLeft: true);

        Assert.Equal(33, status.HighestUsedPercent);
        Assert.Equal(67, status.DisplayPercent);
        Assert.Equal(TraySeverity.Green, status.Severity);
        Assert.Equal("PA: W 94%-6.0d <> S 67%-1h00m", status.PanelText);
        Assert.Contains("Session 67%", status.FullTooltip, StringComparison.Ordinal);
        Assert.Contains("Weekly 94%", status.FullTooltip, StringComparison.Ordinal);
    }

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
        Assert.Equal(TraySeverity.Yellow, result.Severity);
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

    /// <summary>
    /// Every band edge, expressed as the used percentage the tray colours by:
    /// green 0-49, yellow 50-74, orange 75-89, red 90-100.
    /// </summary>
    [Theory]
    [InlineData(0, TraySeverity.Green)]
    [InlineData(49, TraySeverity.Green)]
    [InlineData(50, TraySeverity.Yellow)]
    [InlineData(74, TraySeverity.Yellow)]
    [InlineData(75, TraySeverity.Orange)]
    [InlineData(89, TraySeverity.Orange)]
    [InlineData(90, TraySeverity.Red)]
    [InlineData(100, TraySeverity.Red)]
    public void Compose_maps_highest_used_to_its_band(double used, TraySeverity expected)
    {
        var accounts = new[]
        {
            new AccountUsageStatus("Claude", 100 - used, Now.AddHours(1), null, null)
        };

        var status = TrayStatusComposer.Compose(accounts, Now);

        Assert.Equal(used, status.HighestUsedPercent);
        Assert.Equal(expected, status.Severity);
    }

    [Fact]
    public void An_account_with_no_window_data_stays_unknown()
    {
        var accounts = new[]
        {
            new AccountUsageStatus("Codex", null, null, null, null)
        };

        var status = TrayStatusComposer.Compose(accounts, Now);

        Assert.Null(status.HighestUsedPercent);
        Assert.Equal(TraySeverity.Unknown, status.Severity);
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

    /// <summary>
    /// The provider's own rating is still carried on the record for the remote
    /// payload, but it no longer moves the band: 89% is orange whatever Claude
    /// calls it.
    /// </summary>
    [Fact]
    public void Provider_reported_severity_no_longer_overrides_the_band()
    {
        var accounts = new[]
        {
            new AccountUsageStatus(
                "Claude",
                SessionRemainingPercent: 100,
                SessionResetsAt: DateTimeOffset.UtcNow.AddHours(2),
                WeeklyRemainingPercent: 11,
                WeeklyResetsAt: DateTimeOffset.UtcNow.AddDays(1),
                WeeklySeverity: costats.Core.Pulse.QuotaSeverity.Warning)
        };

        var status = TrayStatusComposer.Compose(accounts, DateTimeOffset.UtcNow);

        Assert.Equal(89, status.HighestUsedPercent);
        Assert.Equal(TraySeverity.Orange, status.Severity);
    }

    /// <summary>
    /// The other direction: a provider calling a window healthy cannot pull a
    /// yellow number back to green.
    /// </summary>
    [Fact]
    public void A_normal_rating_at_71_percent_is_still_yellow()
    {
        var accounts = new[]
        {
            new AccountUsageStatus(
                "Claude",
                SessionRemainingPercent: 29,
                SessionResetsAt: DateTimeOffset.UtcNow.AddHours(2),
                WeeklyRemainingPercent: null,
                WeeklyResetsAt: null,
                SessionSeverity: costats.Core.Pulse.QuotaSeverity.Normal)
        };

        var status = TrayStatusComposer.Compose(accounts, DateTimeOffset.UtcNow);

        Assert.Equal(71, status.HighestUsedPercent);
        Assert.Equal(TraySeverity.Yellow, status.Severity);
    }

    /// <summary>
    /// A critical rating on a low window cannot push the tray to red either.
    /// </summary>
    [Fact]
    public void A_critical_rating_on_a_scoped_window_at_12_percent_is_still_green()
    {
        var accounts = new[]
        {
            new AccountUsageStatus(
                "Claude",
                SessionRemainingPercent: null,
                SessionResetsAt: null,
                WeeklyRemainingPercent: null,
                WeeklyResetsAt: null,
                ScopedQuotas:
                [
                    new costats.Core.Pulse.ScopedQuota("Fable", "weekly", 12, null, true)
                    {
                        Severity = costats.Core.Pulse.QuotaSeverity.Critical
                    }
                ])
        };

        var status = TrayStatusComposer.Compose(accounts, DateTimeOffset.UtcNow);

        Assert.Equal(12, status.HighestUsedPercent);
        Assert.Equal(TraySeverity.Green, status.Severity);
    }

    [Fact]
    public void A_blocked_account_is_red_and_says_so_however_low_its_windows_read()
    {
        var accounts = new[]
        {
            new AccountUsageStatus(
                "Codex",
                SessionRemainingPercent: 99,
                SessionResetsAt: DateTimeOffset.UtcNow.AddHours(1),
                WeeklyRemainingPercent: null,
                WeeklyResetsAt: null,
                IsBlocked: true)
        };

        var status = TrayStatusComposer.Compose(accounts, DateTimeOffset.UtcNow);

        Assert.Equal(TraySeverity.Red, status.Severity);
        Assert.Contains("blocked", status.FullTooltip, StringComparison.Ordinal);
    }
}
