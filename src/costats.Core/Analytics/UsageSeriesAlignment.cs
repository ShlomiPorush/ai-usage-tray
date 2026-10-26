namespace costats.Core.Analytics;

/// <summary>A local day and clock hour, the key hourly points are matched by.</summary>
/// <param name="Day">Local calendar day.</param>
/// <param name="Hour">Local clock hour, 0 to 23.</param>
public readonly record struct UsageClockSlot(DateOnly Day, int Hour);

/// <summary>What the usage chart plots.</summary>
public enum UsageMetric
{
    /// <summary>API list-price estimate in dollars.</summary>
    Cost,

    /// <summary>Processed tokens.</summary>
    Tokens
}

/// <summary>
/// Lays a report's buckets onto a fixed number of chart points, so the
/// current period and its comparison line up point for point.
/// </summary>
/// <remarks>
/// The chart's point count always comes from the current period. A comparison
/// point with no matching instant in its own period is <see cref="double.NaN"/>,
/// which the chart draws as a gap, never as zero: a 25-hour daylight-saving day
/// compared with a 24-hour one has one hour the other side never had.
/// </remarks>
public static class UsageSeriesAlignment
{
    /// <summary>
    /// One value per day from <paramref name="firstDay"/>, for one provider.
    /// Days with no usage are zero; points past the
    /// <paramref name="available"/> days the period has are
    /// <see cref="double.NaN"/>.
    /// </summary>
    public static double[] Daily(
        IEnumerable<DailyModelUsage> buckets,
        UsageProviderKind provider,
        DateOnly firstDay,
        int count,
        UsageMetric metric,
        int? available = null)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        var values = new double[Math.Max(0, count)];
        for (var index = available ?? values.Length; index < values.Length; index++)
        {
            values[index] = double.NaN;
        }

        foreach (var bucket in buckets)
        {
            if (bucket.Provider != provider)
            {
                continue;
            }

            var index = bucket.Day.DayNumber - firstDay.DayNumber;
            if (index >= 0 && index < values.Length && !double.IsNaN(values[index]))
            {
                values[index] += Value(bucket.Totals, metric);
            }
        }

        return values;
    }

    /// <summary>
    /// One value per hour from the start of <paramref name="window"/>, for one
    /// provider. Hours inside the window with no usage are zero; points past
    /// the window's end are <see cref="double.NaN"/>.
    /// </summary>
    public static double[] Hourly(
        IEnumerable<HourlyModelUsage> buckets,
        UsageProviderKind provider,
        UsageTimeWindow window,
        int count,
        UsageMetric metric)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        var values = new double[Math.Max(0, count)];
        var since = window.Since.ToUniversalTime();
        for (var index = 0; index < values.Length; index++)
        {
            if (since.AddHours(index) >= window.Until)
            {
                values[index] = double.NaN;
            }
        }

        foreach (var bucket in buckets)
        {
            if (bucket.Provider != provider)
            {
                continue;
            }

            var offset = (bucket.HourStart.ToUniversalTime() - since).Ticks;
            if (offset < 0 || offset % TimeSpan.TicksPerHour != 0)
            {
                continue;
            }

            var index = offset / TimeSpan.TicksPerHour;
            if (index < values.Length && !double.IsNaN(values[index]))
            {
                values[index] += Value(bucket.Totals, metric);
            }
        }

        return values;
    }

    /// <summary>
    /// An average day of a multi-day period laid under hourly points: each
    /// point gets the comparison's average for the same local clock hour, for
    /// one provider. A clock hour that the current period has twice, on the
    /// day a daylight-saving change repeats it, splits that hour's average
    /// between its two points, so the line never counts it twice.
    /// </summary>
    /// <param name="buckets">The comparison's hourly buckets.</param>
    /// <param name="provider">Which provider's line to build.</param>
    /// <param name="slots">Start of every point of the current period, ascending.</param>
    /// <param name="dayCount">Days the comparison covers, to average over.</param>
    /// <param name="zone">The zone whose clock hours are matched.</param>
    /// <param name="metric">Cost or tokens.</param>
    public static double[] AverageDay(
        IEnumerable<HourlyModelUsage> buckets,
        UsageProviderKind provider,
        IReadOnlyList<DateTimeOffset> slots,
        int dayCount,
        TimeZoneInfo zone,
        UsageMetric metric)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(zone);

        var byHour = new double[24];
        foreach (var bucket in buckets)
        {
            if (bucket.Provider == provider)
            {
                byHour[TimeZoneInfo.ConvertTime(bucket.HourStart, zone).Hour] += Value(bucket.Totals, metric);
            }
        }

        // Every day of the current range gets the full average for its clock
        // hour; only an hour that one day has twice shares it between them.
        var keys = slots
            .Select(slot =>
            {
                var local = TimeZoneInfo.ConvertTime(slot, zone);
                return new UsageClockSlot(DateOnly.FromDateTime(local.DateTime), local.Hour);
            })
            .ToArray();
        var occurrences = keys.GroupBy(key => key).ToDictionary(group => group.Key, group => group.Count());

        var days = Math.Max(1, dayCount);
        return keys
            .Select(key => byHour[key.Hour] / days / occurrences[key])
            .ToArray();
    }

    /// <summary>
    /// Matches every hourly point of a range of whole days to the same day
    /// number and local clock hour in the comparison: day 2 at 14:00 in A
    /// against day 2 at 14:00 in B. Unlike counting elapsed hours, a
    /// daylight-saving change in either period cannot shift everything after
    /// it by an hour. A point past the comparison's last day, or on a clock
    /// hour the comparison's day skipped, is null.
    /// </summary>
    /// <param name="slots">Start of every point of the current period, ascending.</param>
    /// <param name="firstDay">The current period's first local day.</param>
    /// <param name="comparisonFirstDay">The comparison's first local day.</param>
    /// <param name="comparisonDayCount">Days the comparison covers.</param>
    /// <param name="zone">The zone whose days and clock hours are matched.</param>
    public static UsageClockSlot?[] ClockMap(
        IReadOnlyList<DateTimeOffset> slots,
        DateOnly firstDay,
        DateOnly comparisonFirstDay,
        int comparisonDayCount,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(zone);

        return slots
            .Select(slot =>
            {
                var local = TimeZoneInfo.ConvertTime(slot, zone);
                var dayOffset = DateOnly.FromDateTime(local.DateTime).DayNumber - firstDay.DayNumber;
                if (dayOffset < 0 || dayOffset >= comparisonDayCount)
                {
                    return (UsageClockSlot?)null;
                }

                // A clock hour a daylight-saving jump skips on the comparison's
                // day never happened there: a gap, not zero usage.
                var day = comparisonFirstDay.AddDays(dayOffset);
                return zone.IsInvalidTime(day.ToDateTime(new TimeOnly(local.Hour, 0)))
                    ? null
                    : new UsageClockSlot(day, local.Hour);
            })
            .ToArray();
    }

    /// <summary>
    /// The comparison's values on a <see cref="ClockMap"/>: each point gets
    /// the comparison's usage on its matched day and clock hour, zero when
    /// there was none, and NaN past the comparison's end. A clock hour that
    /// the current period has twice splits its value between the two points.
    /// </summary>
    public static double[] ByClock(
        IEnumerable<HourlyModelUsage> buckets,
        UsageProviderKind provider,
        IReadOnlyList<UsageClockSlot?> map,
        TimeZoneInfo zone,
        UsageMetric metric)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(zone);

        var byClock = new Dictionary<UsageClockSlot, double>();
        foreach (var bucket in buckets)
        {
            if (bucket.Provider != provider)
            {
                continue;
            }

            var local = TimeZoneInfo.ConvertTime(bucket.HourStart, zone);
            var key = new UsageClockSlot(DateOnly.FromDateTime(local.DateTime), local.Hour);
            byClock[key] = byClock.GetValueOrDefault(key) + Value(bucket.Totals, metric);
        }

        var occurrences = map
            .OfType<UsageClockSlot>()
            .GroupBy(slot => slot)
            .ToDictionary(group => group.Key, group => group.Count());

        return map
            .Select(slot => slot is { } key
                ? byClock.GetValueOrDefault(key) / occurrences[key]
                : double.NaN)
            .ToArray();
    }

    /// <summary>
    /// The last point of a <see cref="ClockMap"/> that is not in the
    /// comparison's future at <paramref name="now"/>, for
    /// <see cref="WithoutFuture"/>. It is the last point matched to the
    /// current hour, so a clock hour the current period has twice keeps both
    /// halves; or, when the current period has no point for this hour, the
    /// last point before it. Null when nothing on the map lies in the future;
    /// -1 when everything does.
    /// </summary>
    public static int? ClockPartialIndex(IReadOnlyList<UsageClockSlot?> map, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(zone);

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var current = new UsageClockSlot(DateOnly.FromDateTime(local.DateTime), local.Hour);
        var lastNotLater = -1;
        var anyLater = false;
        for (var index = 0; index < map.Count; index++)
        {
            if (map[index] is not { } slot)
            {
                continue;
            }

            if (Compare(slot, current) <= 0)
            {
                lastNotLater = index;
            }
            else
            {
                anyLater = true;
            }
        }

        return anyLater ? lastNotLater : null;

        static int Compare(UsageClockSlot left, UsageClockSlot right)
        {
            var byDay = left.Day.CompareTo(right.Day);
            return byDay != 0 ? byDay : left.Hour.CompareTo(right.Hour);
        }
    }

    /// <summary>
    /// The point of a <see cref="ClockMap"/> to mark "so far": the last one
    /// matched to the comparison's hour that is in progress right now, even
    /// when it is the comparison's last hour. Null when no point stands for
    /// that hour, so a finished hour is never marked as unfinished. This is
    /// not the cut for <see cref="WithoutFuture"/>; that is
    /// <see cref="ClockPartialIndex"/>.
    /// </summary>
    public static int? ClockInProgressIndex(IReadOnlyList<UsageClockSlot?> map, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(zone);

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var current = new UsageClockSlot(DateOnly.FromDateTime(local.DateTime), local.Hour);
        for (var index = map.Count - 1; index >= 0; index--)
        {
            if (map[index] == current)
            {
                return index;
            }
        }

        return null;
    }

    /// <summary>
    /// The point of a period that is still in progress at <paramref name="now"/>:
    /// the current hour of an hourly window, or today in a run of days. Null
    /// when the period is entirely in the past. The rolling 24 hours end at
    /// now, so their last hour is complete and nothing is in progress.
    /// </summary>
    public static int? PartialIndex(UsageResolvedPeriod period, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(zone);

        if (period.Window is { } window)
        {
            return now >= window.Since && now < window.Until
                ? (int)((now - window.Since).Ticks / TimeSpan.TicksPerHour)
                : null;
        }

        var today = UsagePeriods.Today(now, zone);
        return today >= period.FirstDay && today <= period.LastDay
            ? today.DayNumber - period.FirstDay.DayNumber
            : null;
    }

    /// <summary>
    /// Blanks every point after <paramref name="partialIndex"/>: they lie in
    /// the future, and drawing them as zero would show a drop that has not
    /// happened.
    /// </summary>
    public static double[] WithoutFuture(double[] values, int? partialIndex)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (partialIndex is { } index)
        {
            for (var position = Math.Max(0, index + 1); position < values.Length; position++)
            {
                values[position] = double.NaN;
            }
        }

        return values;
    }

    private static double Value(UsageTotals totals, UsageMetric metric) =>
        metric == UsageMetric.Tokens ? totals.Tokens.ProcessedTokens : (double)totals.CostUsd;
}
