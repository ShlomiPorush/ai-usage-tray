namespace costats.Core.Analytics;

/// <summary>What kind of change a <see cref="UsageDelta"/> carries.</summary>
public enum UsageDeltaKind
{
    /// <summary>Nothing to compare: both sides are zero, or comparison is off.</summary>
    None,

    /// <summary>A relative change against a non-zero baseline.</summary>
    Change,

    /// <summary>
    /// The baseline recorded nothing. That is not proof of no usage: older
    /// logs may have been deleted, so this never claims "new".
    /// </summary>
    NoPriorData
}

/// <summary>Which figure a <see cref="UsageDelta"/> compares.</summary>
public enum UsageDeltaBasis
{
    /// <summary>The API list-price estimate.</summary>
    Cost,

    /// <summary>Processed tokens, used when cost cannot be compared fairly.</summary>
    Tokens
}

/// <summary>
/// The change from a comparison period to the current one.
/// </summary>
/// <param name="Kind">What the change means.</param>
/// <param name="Fraction">Relative change for <see cref="UsageDeltaKind.Change"/>: 0.12 is +12%.</param>
/// <param name="Basis">Whether cost or tokens were compared.</param>
public readonly record struct UsageDelta(UsageDeltaKind Kind, double Fraction, UsageDeltaBasis Basis)
{
    /// <summary>No delta to show.</summary>
    public static UsageDelta None => default;

    /// <summary>
    /// Compares two totals by cost when both sides are fully priced, and by
    /// processed tokens otherwise.
    /// </summary>
    /// <remarks>
    /// A cost that leaves out unpriced tokens is a floor, and two floors with
    /// different gaps cannot be compared: $10 plus a lot of unpriced usage
    /// against $20 plus a little is not a 100% rise. Tokens are complete on
    /// both sides, so they are the honest fallback.
    /// </remarks>
    /// <param name="current">The current period's totals.</param>
    /// <param name="previous">The comparison period's totals.</param>
    /// <param name="previousScale">
    /// What the comparison is multiplied by before comparing: the current
    /// period's day count over the comparison's, so periods of different
    /// lengths compare their average per day. 1 for equal lengths.
    /// </param>
    public static UsageDelta Between(UsageTotals current, UsageTotals previous, double previousScale = 1d)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous);

        var useTokens = current.UnpricedTokens > 0 ||
            previous.UnpricedTokens > 0 ||
            (current.CostUsd == 0m && previous.CostUsd == 0m);

        return useTokens
            ? OfTokens(current.Tokens.ProcessedTokens, previous.Tokens.ProcessedTokens, previousScale)
            : OfCost(current.CostUsd, previous.CostUsd, previousScale);
    }

    /// <summary>Compares two token counts, scaling the comparison as <see cref="Between"/> does.</summary>
    public static UsageDelta OfTokens(long current, long previous, double previousScale = 1d) =>
        Of(current, previous * previousScale, UsageDeltaBasis.Tokens);

    /// <summary>Compares two dollar amounts, scaling the comparison as <see cref="Between"/> does.</summary>
    public static UsageDelta OfCost(decimal current, decimal previous, double previousScale = 1d) =>
        Of((double)current, (double)previous * previousScale, UsageDeltaBasis.Cost);

    private static UsageDelta Of(double current, double previous, UsageDeltaBasis basis)
    {
        current = Math.Max(0d, current);
        previous = Math.Max(0d, previous);

        if (previous <= 0d)
        {
            return current <= 0d
                ? None
                : new UsageDelta(UsageDeltaKind.NoPriorData, 0d, basis);
        }

        return new UsageDelta(UsageDeltaKind.Change, (current - previous) / previous, basis);
    }
}
