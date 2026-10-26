namespace costats.Core.Analytics;

/// <summary>
/// The pending selection inside the Usage window's date picker, kept apart
/// from the period on screen until the user applies it.
/// </summary>
/// <remarks>
/// Range mode picks with two plain clicks: the first click sets the anchor and
/// selects that one day, the second closes the range on either side of it, and
/// a third starts over. A shift-click always closes the range against the
/// anchor. Single mode picks one day per click. Days the caller does not allow
/// are ignored, and a range is never allowed to grow past
/// <see cref="MaxSpanDays"/>: the far end is pulled back towards the anchor.
/// </remarks>
public sealed class DateRangeSelection
{
    private readonly Func<DateOnly, bool> _isAllowed;
    private bool _awaitingEnd;

    /// <summary>Creates a selection, optionally starting from an existing range.</summary>
    /// <param name="singleDate">True to pick one day instead of a range.</param>
    /// <param name="maxSpanDays">Longest range allowed, in days.</param>
    /// <param name="isAllowed">Which days can be clicked at all.</param>
    /// <param name="from">The range to start with, or null for none.</param>
    /// <param name="to">End of the starting range.</param>
    public DateRangeSelection(
        bool singleDate,
        int maxSpanDays,
        Func<DateOnly, bool> isAllowed,
        DateOnly? from = null,
        DateOnly? to = null)
    {
        ArgumentNullException.ThrowIfNull(isAllowed);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSpanDays, 1);
        IsSingleDate = singleDate;
        MaxSpanDays = maxSpanDays;
        _isAllowed = isAllowed;

        if (from is { } start)
        {
            var end = singleDate ? start : to ?? start;
            From = start <= end ? start : end;
            To = start <= end ? end : start;
            Anchor = From;
        }
    }

    /// <summary>True when one day is picked instead of a range.</summary>
    public bool IsSingleDate { get; }

    /// <summary>Longest range allowed, in days.</summary>
    public int MaxSpanDays { get; }

    /// <summary>The day the next range click closes against.</summary>
    public DateOnly? Anchor { get; private set; }

    /// <summary>First selected day, or null before any click.</summary>
    public DateOnly? From { get; private set; }

    /// <summary>Last selected day, or null before any click.</summary>
    public DateOnly? To { get; private set; }

    /// <summary>True when a range click is waiting for its second end.</summary>
    public bool IsAwaitingEnd => _awaitingEnd;

    /// <summary>True when there is something to apply.</summary>
    public bool HasSelection => From is not null && To is not null;

    /// <summary>True when <paramref name="day"/> can be clicked.</summary>
    public bool IsAllowed(DateOnly day) => _isAllowed(day);

    /// <summary>True when <paramref name="day"/> is inside the selection.</summary>
    public bool Contains(DateOnly day) =>
        From is { } from && To is { } to && day >= from && day <= to;

    /// <summary>Applies a click on <paramref name="day"/>.</summary>
    /// <param name="day">The clicked day.</param>
    /// <param name="extend">True for a shift-click, which closes the range against the anchor.</param>
    /// <returns>False when the day cannot be picked and nothing changed.</returns>
    public bool Click(DateOnly day, bool extend = false)
    {
        if (!_isAllowed(day))
        {
            return false;
        }

        if (IsSingleDate)
        {
            From = To = Anchor = day;
            return true;
        }

        if ((extend || _awaitingEnd) && Anchor is { } anchor)
        {
            Close(anchor, day);
            _awaitingEnd = false;
            return true;
        }

        From = To = Anchor = day;
        _awaitingEnd = true;
        return true;
    }

    private void Close(DateOnly anchor, DateOnly day)
    {
        var limit = MaxSpanDays - 1;
        if (Math.Abs(day.DayNumber - anchor.DayNumber) > limit)
        {
            day = day > anchor ? anchor.AddDays(limit) : anchor.AddDays(-limit);
        }

        From = day < anchor ? day : anchor;
        To = day < anchor ? anchor : day;
    }

    /// <summary>
    /// The 42 days a six-week month grid shows for <paramref name="year"/> and
    /// <paramref name="month"/>, starting on <paramref name="firstDayOfWeek"/>.
    /// </summary>
    public static IReadOnlyList<DateOnly> MonthGrid(int year, int month, DayOfWeek firstDayOfWeek)
    {
        var first = new DateOnly(year, month, 1);
        var lead = ((int)first.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        var start = first.AddDays(-lead);
        var days = new DateOnly[42];
        for (var index = 0; index < days.Length; index++)
        {
            days[index] = start.AddDays(index);
        }

        return days;
    }
}
