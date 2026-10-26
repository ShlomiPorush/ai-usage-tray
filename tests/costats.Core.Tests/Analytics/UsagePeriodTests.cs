using costats.Core.Analytics;
using Xunit;

namespace costats.Core.Tests.Analytics;

public sealed class UsagePeriodTests
{
    /// <summary>
    /// UTC+2 with daylight saving that starts by skipping local midnight on
    /// Mar 29 (00:00 jumps to 01:00) and ends by repeating it on Oct 25
    /// (01:00 falls back to 00:00), like zones that switch at midnight.
    /// </summary>
    private static readonly TimeZoneInfo MidnightDst = TimeZoneInfo.CreateCustomTimeZone(
        "Test/MidnightDst",
        TimeSpan.FromHours(2),
        "Test midnight DST",
        "Standard",
        "Daylight",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2000, 1, 1),
                new DateTime(2099, 12, 31),
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 29),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 10, 25))
        ]);

    private static readonly TimeZoneInfo PlusThree = TimeZoneInfo.CreateCustomTimeZone(
        "Test+3", TimeSpan.FromHours(3), "Test+3", "Test+3");

    private static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 26);

    [Fact]
    public void Days_normalises_reversed_input()
    {
        var period = UsagePeriod.Days(new DateOnly(2026, 9, 30), new DateOnly(2026, 9, 1));

        Assert.Equal(new DateOnly(2026, 9, 1), period.From);
        Assert.Equal(new DateOnly(2026, 9, 30), period.To);
        Assert.Equal(30, period.DayCount);
        Assert.False(period.IsHourly);
    }

    [Fact]
    public void Short_runs_of_days_and_the_rolling_window_are_hourly()
    {
        Assert.True(UsagePeriod.Day(Today).IsHourly);
        Assert.True(UsagePeriod.Past24Hours.IsHourly);
        Assert.True(UsagePeriod.Days(Today.AddDays(-1), Today).IsHourly);
        Assert.True(UsagePeriod.Days(Today.AddDays(-2), Today).IsHourly);
        Assert.False(UsagePeriod.Days(Today.AddDays(-3), Today).IsHourly);
        Assert.False(UsagePeriod.LastDays(7, Today).IsHourly);
    }

    [Fact]
    public void Two_days_resolve_to_one_48_hour_window_of_whole_local_days()
    {
        var two = UsagePeriods.Resolve(UsagePeriod.Days(new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 19)), Now, PlusThree);

        Assert.Equal(48, two.PointCount);
        Assert.Equal(2, two.DayCount);
        Assert.False(two.IsRolling);
        Assert.Equal(UsagePeriods.StartOfDay(new DateOnly(2026, 10, 18), PlusThree), two.Window!.Value.Since);
        Assert.Equal(UsagePeriods.StartOfDay(new DateOnly(2026, 10, 20), PlusThree), two.Window!.Value.Until);
        Assert.True(UsagePeriods.Resolve(UsagePeriod.Past24Hours, Now, PlusThree).IsRolling);
    }

    [Fact]
    public void A_skipped_midnight_starts_the_day_at_the_first_real_instant()
    {
        // 00:00 on Mar 29 does not exist; the day starts at 01:00 local (+3).
        var start = UsagePeriods.StartOfDay(new DateOnly(2026, 3, 29), MidnightDst);

        Assert.Equal(new DateTimeOffset(2026, 3, 28, 22, 0, 0, TimeSpan.Zero), start);
    }

    [Fact]
    public void A_repeated_midnight_starts_the_day_at_the_earlier_copy()
    {
        // 00:00 on Oct 25 happens twice; the earlier one is still at +3.
        var start = UsagePeriods.StartOfDay(new DateOnly(2026, 10, 25), MidnightDst);

        Assert.Equal(new DateTimeOffset(2026, 10, 24, 21, 0, 0, TimeSpan.Zero), start);
    }

    [Fact]
    public void Daylight_saving_days_have_23_and_25_hourly_buckets()
    {
        Assert.Equal(23, UsagePeriods.DayWindow(new DateOnly(2026, 3, 29), MidnightDst).HourStarts().Count);
        Assert.Equal(25, UsagePeriods.DayWindow(new DateOnly(2026, 10, 25), MidnightDst).HourStarts().Count);
        Assert.Equal(24, UsagePeriods.DayWindow(new DateOnly(2026, 10, 24), MidnightDst).HourStarts().Count);
    }

    [Fact]
    public void Resolve_uses_a_window_only_for_hourly_periods()
    {
        var week = UsagePeriods.Resolve(UsagePeriod.LastDays(7, Today), Now, PlusThree);
        Assert.Null(week.Window);
        Assert.Equal(new DateOnly(2026, 10, 20), week.FirstDay);
        Assert.Equal(7, week.PointCount);

        var day = UsagePeriods.Resolve(UsagePeriod.Day(Today), Now, PlusThree);
        Assert.Equal(UsagePeriods.DayWindow(Today, PlusThree), day.Window);
        Assert.Equal(24, day.PointCount);

        var rolling = UsagePeriods.Resolve(UsagePeriod.Past24Hours, Now, PlusThree);
        Assert.Equal(UsageTimeWindow.LastHours(24, Now), rolling.Window);
    }

    [Fact]
    public void The_previous_period_has_the_same_length_and_ends_the_day_before()
    {
        var week = UsagePeriod.LastDays(7, Today);
        var previous = UsagePeriods.ResolveComparison(week, null, Now, PlusThree);

        Assert.Equal(7, previous.DayCount);
        Assert.Equal(new DateOnly(2026, 10, 13), previous.FirstDay);
        Assert.Equal(new DateOnly(2026, 10, 19), previous.LastDay);
        Assert.Null(previous.Window);
    }

    [Fact]
    public void The_previous_rolling_window_is_the_24_hours_before_it()
    {
        var previous = UsagePeriods.ResolveComparison(UsagePeriod.Past24Hours, null, Now, PlusThree);
        var current = UsageTimeWindow.LastHours(24, Now);

        Assert.Equal(1, previous.DayCount);
        Assert.Equal(current.Since.AddHours(-24), previous.Window!.Value.Since);
        Assert.Equal(current.Since, previous.Window!.Value.Until);
    }

    [Fact]
    public void Picked_days_of_any_length_are_the_comparison()
    {
        var month = UsagePeriod.Days(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));
        var compared = UsagePeriods.ResolveComparison(
            month,
            UsagePeriod.Days(new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 1)),
            Now,
            PlusThree);

        Assert.Equal(new DateOnly(2026, 6, 1), compared.FirstDay);
        Assert.Equal(new DateOnly(2026, 6, 10), compared.LastDay);
        Assert.Equal(10, compared.DayCount);
        Assert.Null(compared.Window);
    }

    [Fact]
    public void Picked_days_under_an_hourly_range_are_reported_by_hour_over_whole_local_days()
    {
        // Mar 29 skips midnight in this zone; the window still starts and ends
        // on real local day starts.
        var picked = UsagePeriod.Days(new DateOnly(2026, 3, 28), new DateOnly(2026, 3, 30));
        var compared = UsagePeriods.ResolveComparison(UsagePeriod.Day(Today), picked, Now, MidnightDst);

        Assert.Equal(3, compared.DayCount);
        Assert.Equal(UsagePeriods.StartOfDay(new DateOnly(2026, 3, 28), MidnightDst), compared.Window!.Value.Since);
        Assert.Equal(UsagePeriods.StartOfDay(new DateOnly(2026, 3, 31), MidnightDst), compared.Window!.Value.Until);
        Assert.Equal(71, compared.Window!.Value.HourStarts().Count);
    }

    [Fact]
    public void Day_and_hour_rows_outside_the_current_year_carry_the_year()
    {
        var report = new UsageReport
        {
            Daily = [new DailyUsage(new DateOnly(2025, 10, 1), UsageTotals.Empty), new DailyUsage(new DateOnly(2026, 10, 1), UsageTotals.Empty)],
            Hourly = [new HourlyUsage(new DateTimeOffset(2025, 10, 1, 11, 0, 0, TimeSpan.Zero), UsageTotals.Empty)]
        };

        Assert.Equal(["Wed, Oct 1, 2025", "Thu, Oct 1"], UsageBreakdown.Days(report, Today).Select(row => row.Label));
        Assert.Equal("Wed, Oct 1, 2025, 14:00", Assert.Single(UsageBreakdown.Hours(report, PlusThree, Today)).Label);
    }

    [Fact]
    public void A_repeated_hour_carries_its_utc_offset()
    {
        var first = UsageNumberFormat.LongHourLabel(new DateTimeOffset(2026, 10, 24, 21, 0, 0, TimeSpan.Zero), MidnightDst);
        var second = UsageNumberFormat.LongHourLabel(new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero), MidnightDst);
        var plain = UsageNumberFormat.LongHourLabel(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero), MidnightDst);

        Assert.Equal("Sun, Oct 25, 00:00 (UTC+3)", first);
        Assert.Equal("Sun, Oct 25, 00:00 (UTC+2)", second);
        Assert.Equal("Sun, Oct 25, 11:00", plain);
    }

    [Fact]
    public void Range_labels_add_the_year_only_outside_the_current_one()
    {
        Assert.Equal("Sep 1 to Sep 30", UsageNumberFormat.RangeLabel(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), Today));
        Assert.Equal("Dec 20, 2025 to Jan 5, 2026", UsageNumberFormat.RangeLabel(new DateOnly(2025, 12, 20), new DateOnly(2026, 1, 5), Today));
        Assert.Equal("Sat, Aug 22", UsageNumberFormat.RangeLabel(new DateOnly(2026, 8, 22), new DateOnly(2026, 8, 22), Today));
    }

    [Fact]
    public void Custom_ranges_stay_inside_the_picker_limits()
    {
        Assert.True(UsagePeriods.IsValidCustomRange(Today, Today, Today));
        Assert.False(UsagePeriods.IsValidCustomRange(Today, Today.AddDays(1), Today));
        Assert.False(UsagePeriods.IsValidCustomRange(Today.AddDays(-UsagePeriods.MaxLookbackDays - 1), Today.AddDays(-700), Today));
        Assert.True(UsagePeriods.IsValidCustomRange(Today.AddDays(-UsagePeriods.MaxCustomDays + 1), Today, Today));
        Assert.False(UsagePeriods.IsValidCustomRange(Today.AddDays(-UsagePeriods.MaxCustomDays), Today, Today));
    }
}
