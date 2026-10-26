namespace costats.Core.Analytics;

/// <summary>
/// How a comparison's changes are measured: always from the earlier period
/// to the later one, whichever of the two the user named A, and as an
/// average per day when the periods differ in length.
/// </summary>
/// <remarks>
/// A change only reads naturally forward in time. Picking A as September and
/// B as October, $413 then $870 is a rise, and so is the default view where A
/// is the range on screen and B the period before it.
/// </remarks>
/// <param name="CurrentIsEarlier">True when the current period (A) comes first.</param>
/// <param name="PreviousScale">
/// The current period's day count over the comparison's, as for
/// <see cref="UsageDelta.Between"/>.
/// </param>
public readonly record struct UsageChangeDirection(bool CurrentIsEarlier, double PreviousScale)
{
    /// <summary>Equal lengths, comparison first: the plain "against the period before".</summary>
    public static UsageChangeDirection Default => new(false, 1d);

    /// <summary>
    /// The direction for two resolved periods. The one that starts first is
    /// the earlier; a tie goes to the one that ends first, and a full tie
    /// keeps the comparison as the base. Starts and ends are real instants:
    /// a window's own bounds, or local midnights in <paramref name="zone"/>.
    /// </summary>
    public static UsageChangeDirection For(UsageResolvedPeriod current, UsageResolvedPeriod comparison, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(zone);

        var currentStart = current.Window?.Since ?? UsagePeriods.StartOfDay(current.FirstDay, zone);
        var comparisonStart = comparison.Window?.Since ?? UsagePeriods.StartOfDay(comparison.FirstDay, zone);
        var currentEnd = current.Window?.Until ?? UsagePeriods.StartOfDay(current.LastDay.AddDays(1), zone);
        var comparisonEnd = comparison.Window?.Until ?? UsagePeriods.StartOfDay(comparison.LastDay.AddDays(1), zone);
        var currentIsEarlier = currentStart != comparisonStart
            ? currentStart < comparisonStart
            : currentEnd < comparisonEnd;

        var scale = comparison.DayCount > 0 ? (double)current.DayCount / comparison.DayCount : 1d;
        return new UsageChangeDirection(currentIsEarlier, scale);
    }

    /// <summary>"A → B" when A comes first, "B → A" otherwise.</summary>
    public string Label => CurrentIsEarlier ? "A → B" : "B → A";

    /// <summary>The change between two totals, measured forward in time.</summary>
    public UsageDelta Between(UsageTotals current, UsageTotals previous) => CurrentIsEarlier
        ? UsageDelta.Between(previous, current, InverseScale)
        : UsageDelta.Between(current, previous, PreviousScale);

    /// <summary>The change between two token counts, measured forward in time.</summary>
    public UsageDelta OfTokens(long current, long previous) => CurrentIsEarlier
        ? UsageDelta.OfTokens(previous, current, InverseScale)
        : UsageDelta.OfTokens(current, previous, PreviousScale);

    /// <summary>The change between two dollar amounts, measured forward in time.</summary>
    public UsageDelta OfCost(decimal current, decimal previous) => CurrentIsEarlier
        ? UsageDelta.OfCost(previous, current, InverseScale)
        : UsageDelta.OfCost(current, previous, PreviousScale);

    private double InverseScale => PreviousScale > 0d ? 1d / PreviousScale : 1d;
}
