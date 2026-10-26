namespace costats.Core.Analytics;

/// <summary>
/// What the Usage window shows: the rolling past 24 hours, or a run of whole
/// local calendar days. A single day is shown hourly, like the rolling window.
/// </summary>
/// <remarks>
/// A period is relative to nothing: <see cref="UsagePeriods.Resolve"/> turns it
/// into the exact days or instants to ask the analytics service for, against
/// one clock reading and one time zone.
/// </remarks>
public readonly record struct UsagePeriod
{
    private UsagePeriod(bool isRolling, DateOnly from, DateOnly to)
    {
        IsRolling = isRolling;
        From = from;
        To = to;
    }

    /// <summary>The rolling past 24 hours, ending now.</summary>
    public static UsagePeriod Past24Hours { get; } = new(true, default, default);

    /// <summary>True for <see cref="Past24Hours"/>.</summary>
    public bool IsRolling { get; }

    /// <summary>First local day included. Unused for <see cref="Past24Hours"/>.</summary>
    public DateOnly From { get; }

    /// <summary>Last local day included. Unused for <see cref="Past24Hours"/>.</summary>
    public DateOnly To { get; }

    /// <summary>Whole local days from one date to another, in either order.</summary>
    public static UsagePeriod Days(DateOnly first, DateOnly second) =>
        first <= second ? new(false, first, second) : new(false, second, first);

    /// <summary>One whole local day.</summary>
    public static UsagePeriod Day(DateOnly day) => new(false, day, day);

    /// <summary>
    /// The last <paramref name="days"/> local days ending on
    /// <paramref name="today"/>, inclusive.
    /// </summary>
    public static UsagePeriod LastDays(int days, DateOnly today)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        return new(false, today.AddDays(-(days - 1)), today);
    }

    /// <summary>Calendar days covered; the rolling window counts as one.</summary>
    public int DayCount => IsRolling ? 1 : To.DayNumber - From.DayNumber + 1;

    /// <summary>True for a run of exactly one local day.</summary>
    public bool IsSingleDay => !IsRolling && From == To;

    /// <summary>
    /// True when the period is bucketed by hour rather than by day: the
    /// rolling 24 hours, and any run of up to
    /// <see cref="UsagePeriods.MaxHourlyDays"/> days, where a point per day
    /// would leave the chart with only two or three points.
    /// </summary>
    public bool IsHourly => IsRolling || DayCount <= UsagePeriods.MaxHourlyDays;

    /// <summary>The same kind of period moved by whole days.</summary>
    public UsagePeriod ShiftDays(int days) =>
        IsRolling ? this : new(false, From.AddDays(days), To.AddDays(days));
}

/// <summary>
/// A period turned into what the analytics service is asked for.
/// </summary>
/// <param name="FirstDay">First local day touched, for labels.</param>
/// <param name="LastDay">Last local day touched, for labels.</param>
/// <param name="Window">
/// The exact hourly window, or null for a daily range from
/// <paramref name="FirstDay"/> to <paramref name="LastDay"/>.
/// </param>
/// <param name="DayCount">
/// How many days the period stands for, for per-day averages: the rolling
/// 24 hours count as one.
/// </param>
public sealed record UsageResolvedPeriod(DateOnly FirstDay, DateOnly LastDay, UsageTimeWindow? Window, int DayCount)
{
    /// <summary>
    /// True for a rolling 24-hour window, whose bounds are clock times rather
    /// than local midnights.
    /// </summary>
    public bool IsRolling { get; init; }

    /// <summary>True when the report is bucketed by hour.</summary>
    public bool IsHourly => Window is not null;

    /// <summary>The day range a daily report covers.</summary>
    public UsageDateRange Range => new(FirstDay, LastDay);

    /// <summary>Number of chart points: one per hour of the window, or one per day.</summary>
    public int PointCount => Window is { } window
        ? window.HourStarts().Count
        : LastDay.DayNumber - FirstDay.DayNumber + 1;
}

/// <summary>
/// Resolves <see cref="UsagePeriod"/> values and their comparison periods
/// against one clock reading and one time zone, so every part of one load
/// agrees on what "today" and "midnight" mean.
/// </summary>
public static class UsagePeriods
{
    /// <summary>Longest custom range the picker accepts, in days.</summary>
    public const int MaxCustomDays = 366;

    /// <summary>How far back a custom range may start, in days before today.</summary>
    public const int MaxLookbackDays = 730;

    /// <summary>Longest run of days that is still shown by hour.</summary>
    public const int MaxHourlyDays = 3;

    /// <summary>The local calendar day <paramref name="now"/> falls on.</summary>
    public static DateOnly Today(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
    }

    /// <summary>
    /// The first instant of a local day. A midnight that a daylight-saving
    /// jump skips starts the day at the first local time that exists; a
    /// midnight that happens twice starts it at the earlier of the two.
    /// </summary>
    public static DateTimeOffset StartOfDay(DateOnly day, TimeZoneInfo zone) =>
        LocalInstant(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone);

    /// <summary>
    /// The instant a local wall-clock time stands for. A time a
    /// daylight-saving jump skips moves forward to the first one that exists;
    /// a time that happens twice resolves to the earlier of the two.
    /// </summary>
    public static DateTimeOffset LocalInstant(DateTime local, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        // Gaps are whole minutes and never longer than a few hours, so a
        // minute walk finds the end of one quickly and exactly.
        for (var guard = 0; zone.IsInvalidTime(local) && guard < 24 * 60; guard++)
        {
            local = local.AddMinutes(1);
        }

        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);

        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>
    /// One whole local day as an exact window: 24 hourly buckets on most days,
    /// 23 or 25 on a daylight-saving change.
    /// </summary>
    public static UsageTimeWindow DayWindow(DateOnly day, TimeZoneInfo zone) =>
        new(StartOfDay(day, zone), StartOfDay(day.AddDays(1), zone));

    /// <summary>Turns a period into the range or window to report on.</summary>
    public static UsageResolvedPeriod Resolve(UsagePeriod period, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (period.IsRolling)
        {
            var window = UsageTimeWindow.LastHours(24, now);
            return new UsageResolvedPeriod(LocalDay(window.Since, zone), LocalDay(window.Until, zone), window, 1)
            {
                IsRolling = true
            };
        }

        return period.IsHourly
            ? new UsageResolvedPeriod(period.From, period.To, DaysWindow(period.From, period.To, zone), period.DayCount)
            : new UsageResolvedPeriod(period.From, period.To, null, period.DayCount);
    }

    /// <summary>Whole local days as one exact window, from the first midnight to the one after the last day.</summary>
    public static UsageTimeWindow DaysWindow(DateOnly from, DateOnly to, TimeZoneInfo zone) =>
        new(StartOfDay(from, zone), StartOfDay(to.AddDays(1), zone));

    /// <summary>
    /// The period <paramref name="period"/> is compared with: the one right
    /// before it, of the same length, when <paramref name="picked"/> is null,
    /// or the whole days the user picked, of any length.
    /// </summary>
    /// <remarks>
    /// The previous rolling window is the exact 24 hours before the current
    /// one. Picked days are reported by hour when the current period is
    /// hourly, so the chart can line them up hour by hour, or lay an average
    /// day of them under it when they are longer, and by day otherwise.
    /// </remarks>
    public static UsageResolvedPeriod ResolveComparison(
        UsagePeriod period,
        UsagePeriod? picked,
        DateTimeOffset now,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (picked is { } days && !days.IsRolling)
        {
            return period.IsHourly
                ? new UsageResolvedPeriod(days.From, days.To, DaysWindow(days.From, days.To, zone), days.DayCount)
                : new UsageResolvedPeriod(days.From, days.To, null, days.DayCount);
        }

        if (period.IsRolling)
        {
            var primary = UsageTimeWindow.LastHours(24, now);
            var previous = new UsageTimeWindow(primary.Since.AddHours(-24), primary.Since);
            return new UsageResolvedPeriod(LocalDay(previous.Since, zone), LocalDay(previous.Until, zone), previous, 1)
            {
                IsRolling = true
            };
        }

        return Resolve(period.ShiftDays(-period.DayCount), now, zone);
    }

    /// <summary>
    /// True when a custom range is inside what the picker offers: no future
    /// day, no earlier than <see cref="MaxLookbackDays"/>, and no longer than
    /// <see cref="MaxCustomDays"/>.
    /// </summary>
    public static bool IsValidCustomRange(DateOnly from, DateOnly to, DateOnly today) =>
        from <= to &&
        to <= today &&
        from >= today.AddDays(-MaxLookbackDays) &&
        to.DayNumber - from.DayNumber + 1 <= MaxCustomDays;

    /// <summary>The first local day a period touches.</summary>
    public static DateOnly FirstDay(UsagePeriod period, DateTimeOffset now, TimeZoneInfo zone) =>
        period.IsRolling ? LocalDay(UsageTimeWindow.LastHours(24, now).Since, zone) : period.From;

    private static DateOnly LocalDay(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
}
