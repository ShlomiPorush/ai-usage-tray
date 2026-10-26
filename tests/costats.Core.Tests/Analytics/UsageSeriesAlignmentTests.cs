using costats.Core.Analytics;
using Xunit;

namespace costats.Core.Tests.Analytics;

public sealed class UsageSeriesAlignmentTests
{
    private static readonly DateTimeOffset Since = new(2026, 10, 24, 21, 0, 0, TimeSpan.Zero);

    private static UsageTotals Totals(decimal cost, long tokens) => new()
    {
        CostUsd = cost,
        Tokens = new UsageTokens { OutputTokens = tokens }
    };

    private static HourlyModelUsage Hour(int offset, decimal cost, UsageProviderKind provider = UsageProviderKind.Claude) =>
        new(Since.AddHours(offset), provider, "model", Totals(cost, 0));

    [Fact]
    public void Daily_values_land_on_their_day_offset_for_one_provider()
    {
        var first = new DateOnly(2026, 10, 1);
        var buckets = new[]
        {
            new DailyModelUsage(first.AddDays(2), UsageProviderKind.Claude, "a", Totals(3m, 30)),
            new DailyModelUsage(first.AddDays(2), UsageProviderKind.Claude, "b", Totals(1m, 10)),
            new DailyModelUsage(first.AddDays(1), UsageProviderKind.Codex, "c", Totals(9m, 90)),
            new DailyModelUsage(first.AddDays(9), UsageProviderKind.Claude, "d", Totals(9m, 90))
        };

        var cost = UsageSeriesAlignment.Daily(buckets, UsageProviderKind.Claude, first, 3, UsageMetric.Cost);
        var tokens = UsageSeriesAlignment.Daily(buckets, UsageProviderKind.Claude, first, 3, UsageMetric.Tokens);

        Assert.Equal([0d, 0d, 4d], cost);
        Assert.Equal([0d, 0d, 40d], tokens);
    }

    [Fact]
    public void A_shorter_daily_comparison_leaves_a_gap_past_its_last_day()
    {
        var first = new DateOnly(2026, 8, 1);
        var buckets = new[] { new DailyModelUsage(first.AddDays(1), UsageProviderKind.Claude, "a", Totals(2m, 0)) };

        var values = UsageSeriesAlignment.Daily(buckets, UsageProviderKind.Claude, first, 5, UsageMetric.Cost, available: 3);

        Assert.Equal(0d, values[0]);
        Assert.Equal(2d, values[1]);
        Assert.Equal(0d, values[2]);
        Assert.True(double.IsNaN(values[3]) && double.IsNaN(values[4]));
    }

    [Fact]
    public void An_average_day_matches_points_by_local_clock_hour()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+3", TimeSpan.FromHours(3), "Test+3", "Test+3");

        // Two days of comparison: $4 then $2 at 10:00 local, $6 once at 11:00.
        var buckets = new[]
        {
            new HourlyModelUsage(new DateTimeOffset(2026, 8, 1, 7, 0, 0, TimeSpan.Zero), UsageProviderKind.Claude, "a", Totals(4m, 0)),
            new HourlyModelUsage(new DateTimeOffset(2026, 8, 2, 7, 0, 0, TimeSpan.Zero), UsageProviderKind.Claude, "a", Totals(2m, 0)),
            new HourlyModelUsage(new DateTimeOffset(2026, 8, 2, 8, 0, 0, TimeSpan.Zero), UsageProviderKind.Claude, "a", Totals(6m, 0)),
            new HourlyModelUsage(new DateTimeOffset(2026, 8, 2, 8, 0, 0, TimeSpan.Zero), UsageProviderKind.Codex, "b", Totals(9m, 0))
        };

        // Current points at 10:30, 11:30 and 12:30 local.
        var slots = new[]
        {
            new DateTimeOffset(2026, 10, 26, 7, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 26, 8, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 26, 9, 30, 0, TimeSpan.Zero)
        };

        var values = UsageSeriesAlignment.AverageDay(buckets, UsageProviderKind.Claude, slots, 2, zone, UsageMetric.Cost);

        Assert.Equal([3d, 3d, 0d], values);
    }

    [Fact]
    public void A_repeated_clock_hour_shares_its_average_instead_of_doubling_it()
    {
        // UTC+2 with daylight saving ending Oct 25 at 01:00, so 00:00 local
        // happens twice. A 25-hour day compared with itself must keep its total.
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Test/RepeatedMidnight",
            TimeSpan.FromHours(2),
            "Test",
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
        var window = UsagePeriods.DayWindow(new DateOnly(2026, 10, 25), zone);
        var slots = window.HourStarts();
        var buckets = new[]
        {
            new HourlyModelUsage(slots[0], UsageProviderKind.Claude, "a", Totals(1m, 0)),
            new HourlyModelUsage(slots[1], UsageProviderKind.Claude, "a", Totals(3m, 0))
        };

        var values = UsageSeriesAlignment.AverageDay(buckets, UsageProviderKind.Claude, slots, 1, zone, UsageMetric.Cost);

        Assert.Equal(25, values.Length);
        Assert.Equal(2d, values[0]);
        Assert.Equal(2d, values[1]);
        Assert.Equal(4d, values.Sum());
    }

    [Fact]
    public void Today_in_a_run_of_days_is_the_point_in_progress()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+3", TimeSpan.FromHours(3), "Test+3", "Test+3");
        var now = new DateTimeOffset(2026, 10, 26, 13, 0, 0, TimeSpan.Zero);
        var withToday = new UsageResolvedPeriod(new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 26), null, 2);
        var past = new UsageResolvedPeriod(new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 19), null, 2);

        Assert.Equal(1, UsageSeriesAlignment.PartialIndex(withToday, now, zone));
        Assert.Null(UsageSeriesAlignment.PartialIndex(past, now, zone));
    }

    [Fact]
    public void The_current_hour_of_today_is_in_progress_and_later_hours_are_future()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+3", TimeSpan.FromHours(3), "Test+3", "Test+3");
        var now = new DateTimeOffset(2026, 10, 26, 13, 20, 0, TimeSpan.Zero); // 16:20 local
        var today = new UsageResolvedPeriod(
            new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 26), UsagePeriods.DayWindow(new DateOnly(2026, 10, 26), zone), 1);

        var partial = UsageSeriesAlignment.PartialIndex(today, now, zone);
        var values = UsageSeriesAlignment.WithoutFuture(Enumerable.Repeat(1d, 24).ToArray(), partial);

        Assert.Equal(16, partial);
        Assert.Equal(1d, values[16]);
        Assert.True(values.Skip(17).All(double.IsNaN));
    }

    [Fact]
    public void The_rolling_24_hours_have_nothing_in_progress()
    {
        var now = new DateTimeOffset(2026, 10, 26, 13, 20, 0, TimeSpan.Zero);
        var window = UsageTimeWindow.LastHours(24, now);
        var rolling = new UsageResolvedPeriod(new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 26), window, 1);

        Assert.Null(UsageSeriesAlignment.PartialIndex(rolling, now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Two_days_match_two_days_by_clock_across_a_dst_change()
    {
        // Israel-like: daylight saving ends Oct 25 at 02:00 (+3 to +2), so
        // B's first day has 25 hours. Day 2 at 00:00 in A must face day 2 at
        // 00:00 in B, not 23:00 on B's first day.
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Test/IsraelLike",
            TimeSpan.FromHours(2),
            "Test",
            "Standard",
            "Daylight",
            [
                TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                    new DateTime(2000, 1, 1),
                    new DateTime(2099, 12, 31),
                    TimeSpan.FromHours(1),
                    TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 27),
                    TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 10, 25))
            ]);
        var aFirst = new DateOnly(2026, 10, 18);
        var bFirst = new DateOnly(2026, 10, 25);
        var slots = UsagePeriods.DaysWindow(aFirst, aFirst.AddDays(1), zone).HourStarts();

        var map = UsageSeriesAlignment.ClockMap(slots, aFirst, bFirst, 2, zone);

        Assert.Equal(48, map.Length);
        Assert.Equal(new UsageClockSlot(new DateOnly(2026, 10, 26), 0), map[24]);
        Assert.Equal(new UsageClockSlot(new DateOnly(2026, 10, 26), 23), map[47]);

        // B's usage at 26th 00:00 local lands on A's day-2 midnight point.
        var bMidnight = UsagePeriods.StartOfDay(new DateOnly(2026, 10, 26), zone);
        var values = UsageSeriesAlignment.ByClock(
            [new HourlyModelUsage(bMidnight, UsageProviderKind.Claude, "m", Totals(7m, 0))],
            UsageProviderKind.Claude,
            map,
            zone,
            UsageMetric.Cost);
        Assert.Equal(7d, values[24]);
        Assert.Equal(7d, values.Sum());
    }

    [Fact]
    public void An_average_day_under_several_days_is_not_divided_again_by_their_count()
    {
        // B: two days with $8 at 10:00 each, an average of $8 at 10:00. Both of
        // A's days must show 8 at 10:00, not 4.
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+3", TimeSpan.FromHours(3), "Test+3", "Test+3");
        var b10 = new DateTimeOffset(2026, 8, 1, 7, 0, 0, TimeSpan.Zero);
        var buckets = new[]
        {
            new HourlyModelUsage(b10, UsageProviderKind.Claude, "a", Totals(8m, 0)),
            new HourlyModelUsage(b10.AddDays(1), UsageProviderKind.Claude, "a", Totals(8m, 0))
        };
        var slots = UsagePeriods.DaysWindow(new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 19), zone).HourStarts();

        var values = UsageSeriesAlignment.AverageDay(buckets, UsageProviderKind.Claude, slots, 2, zone, UsageMetric.Cost);

        Assert.Equal(8d, values[10]);
        Assert.Equal(8d, values[34]);
    }

    [Fact]
    public void A_clock_hour_a_has_twice_keeps_both_halves_of_bs_hour_in_progress()
    {
        // A: Oct 25 in a zone that repeats midnight, so A's first two points
        // are both 00:00. B is now in its 00:00 hour with $10: both points
        // stay, 5 and 5, instead of the second being cut as future.
        var zone = MidnightDst();
        var slots = UsagePeriods.DayWindow(new DateOnly(2026, 10, 25), zone).HourStarts();
        var bDay = new DateOnly(2026, 11, 2);
        var map = UsageSeriesAlignment.ClockMap(slots, new DateOnly(2026, 10, 25), bDay, 1, zone);
        var bMidnight = UsagePeriods.StartOfDay(bDay, zone);
        var now = bMidnight.AddMinutes(30);

        var partial = UsageSeriesAlignment.ClockPartialIndex(map, now, zone);
        var values = UsageSeriesAlignment.WithoutFuture(
            UsageSeriesAlignment.ByClock([new HourlyModelUsage(bMidnight, UsageProviderKind.Claude, "m", Totals(10m, 0))], UsageProviderKind.Claude, map, zone, UsageMetric.Cost),
            partial);

        Assert.Equal(1, partial);
        Assert.Equal(5d, values[0]);
        Assert.Equal(5d, values[1]);
        Assert.True(double.IsNaN(values[2]));
    }

    [Fact]
    public void An_hour_a_skips_still_hides_bs_future()
    {
        // A: Mar 29, which skips midnight, so it has no 00:00 point. B is now
        // in its 00:00 hour: all of A's points map to later hours, so all of B
        // is future and nothing is drawn as a zero drop.
        var zone = MidnightDst();
        var slots = UsagePeriods.DayWindow(new DateOnly(2026, 3, 29), zone).HourStarts();
        var bDay = new DateOnly(2026, 4, 5);
        var map = UsageSeriesAlignment.ClockMap(slots, new DateOnly(2026, 3, 29), bDay, 1, zone);
        var now = UsagePeriods.StartOfDay(bDay, zone).AddMinutes(30);

        var partial = UsageSeriesAlignment.ClockPartialIndex(map, now, zone);
        var values = UsageSeriesAlignment.WithoutFuture(new double[slots.Count], partial);

        Assert.Equal(-1, partial);
        Assert.All(values, value => Assert.True(double.IsNaN(value)));
    }

    [Fact]
    public void Bs_last_hour_in_progress_is_still_marked_so_far()
    {
        // B is now at 23:30 on its last day: nothing is later, so nothing is
        // cut, but the 23:00 point is still the hour in progress.
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+3", TimeSpan.FromHours(3), "Test+3", "Test+3");
        var aFirst = new DateOnly(2026, 10, 18);
        var bFirst = new DateOnly(2026, 10, 25);
        var slots = UsagePeriods.DaysWindow(aFirst, aFirst.AddDays(1), zone).HourStarts();
        var map = UsageSeriesAlignment.ClockMap(slots, aFirst, bFirst, 2, zone);
        var now = UsagePeriods.StartOfDay(bFirst.AddDays(1), zone).AddHours(23).AddMinutes(30);

        Assert.Null(UsageSeriesAlignment.ClockPartialIndex(map, now, zone));
        Assert.Equal(47, UsageSeriesAlignment.ClockInProgressIndex(map, now, zone));
    }

    [Fact]
    public void A_finished_hour_before_a_skipped_one_is_not_marked_so_far()
    {
        // A skips midnight (Mar 29), B is now at 00:30: the cut hides all of
        // B, and no point is marked in progress, since A has no 00:00 point.
        var zone = MidnightDst();
        var slots = UsagePeriods.DayWindow(new DateOnly(2026, 3, 29), zone).HourStarts();
        var bDay = new DateOnly(2026, 4, 5);
        var map = UsageSeriesAlignment.ClockMap(slots, new DateOnly(2026, 3, 29), bDay, 1, zone);
        var now = UsagePeriods.StartOfDay(bDay, zone).AddMinutes(30);

        Assert.Equal(-1, UsageSeriesAlignment.ClockPartialIndex(map, now, zone));
        Assert.Null(UsageSeriesAlignment.ClockInProgressIndex(map, now, zone));
    }

    [Fact]
    public void A_clock_hour_the_comparison_skipped_is_a_gap()
    {
        // B: Mar 29, which skips 00:00. A's 00:00 point has no counterpart.
        var zone = MidnightDst();
        var slots = UsagePeriods.DayWindow(new DateOnly(2026, 4, 5), zone).HourStarts();

        var map = UsageSeriesAlignment.ClockMap(slots, new DateOnly(2026, 4, 5), new DateOnly(2026, 3, 29), 1, zone);
        var values = UsageSeriesAlignment.ByClock([], UsageProviderKind.Claude, map, zone, UsageMetric.Cost);

        Assert.Null(map[0]);
        Assert.True(double.IsNaN(values[0]));
        Assert.Equal(new UsageClockSlot(new DateOnly(2026, 3, 29), 1), map[1]);
    }

    private static TimeZoneInfo MidnightDst() => TimeZoneInfo.CreateCustomTimeZone(
        "Test/MidnightDst",
        TimeSpan.FromHours(2),
        "Test",
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

    [Fact]
    public void Clock_matching_past_a_shorter_comparison_is_a_gap()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+3", TimeSpan.FromHours(3), "Test+3", "Test+3");
        var aFirst = new DateOnly(2026, 10, 1);
        var slots = UsagePeriods.DaysWindow(aFirst, aFirst.AddDays(1), zone).HourStarts();

        var map = UsageSeriesAlignment.ClockMap(slots, aFirst, new DateOnly(2026, 9, 1), 1, zone);
        var values = UsageSeriesAlignment.ByClock([], UsageProviderKind.Claude, map, zone, UsageMetric.Cost);

        Assert.All(map.Take(24), slot => Assert.NotNull(slot));
        Assert.All(map.Skip(24), slot => Assert.Null(slot));
        Assert.True(values.Skip(24).All(double.IsNaN));
        Assert.Equal(0d, values[0]);
    }

    [Fact]
    public void The_matched_comparison_hour_in_progress_is_found_on_the_map()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+3", TimeSpan.FromHours(3), "Test+3", "Test+3");
        var aFirst = new DateOnly(2026, 10, 18);
        var slots = UsagePeriods.DaysWindow(aFirst, aFirst.AddDays(1), zone).HourStarts();
        var map = UsageSeriesAlignment.ClockMap(slots, aFirst, new DateOnly(2026, 10, 25), 2, zone);

        // Oct 26 16:20 local: day 2, hour 16.
        var now = new DateTimeOffset(2026, 10, 26, 13, 20, 0, TimeSpan.Zero);

        Assert.Equal(24 + 16, UsageSeriesAlignment.ClockPartialIndex(map, now, zone));
    }

    [Fact]
    public void A_shorter_comparison_window_leaves_a_gap_not_a_zero()
    {
        // A 25-hour current day compared with a 24-hour one: the 25th point
        // has no counterpart and must be NaN.
        var comparison = new UsageTimeWindow(Since, Since.AddHours(24));
        var values = UsageSeriesAlignment.Hourly([Hour(0, 1m), Hour(23, 2m)], UsageProviderKind.Claude, comparison, 25, UsageMetric.Cost);

        Assert.Equal(25, values.Length);
        Assert.Equal(1d, values[0]);
        Assert.Equal(2d, values[23]);
        Assert.True(double.IsNaN(values[24]));
        Assert.Equal(0d, values[12]);
    }

    [Fact]
    public void A_longer_comparison_window_is_cut_to_the_current_point_count()
    {
        // A 23-hour current day compared with a 24-hour one: the extra hour
        // has no point on the axis and is left out of the line.
        var comparison = new UsageTimeWindow(Since, Since.AddHours(24));
        var values = UsageSeriesAlignment.Hourly([Hour(22, 1m), Hour(23, 5m)], UsageProviderKind.Claude, comparison, 23, UsageMetric.Cost);

        Assert.Equal(23, values.Length);
        Assert.Equal(1d, values[22]);
        Assert.DoesNotContain(values, double.IsNaN);
    }

    [Fact]
    public void Hourly_values_ignore_other_providers()
    {
        var window = new UsageTimeWindow(Since, Since.AddHours(3));
        var values = UsageSeriesAlignment.Hourly(
            [Hour(1, 4m, UsageProviderKind.Codex), Hour(1, 1m)],
            UsageProviderKind.Claude,
            window,
            3,
            UsageMetric.Cost);

        Assert.Equal([0d, 1d, 0d], values);
    }
}
