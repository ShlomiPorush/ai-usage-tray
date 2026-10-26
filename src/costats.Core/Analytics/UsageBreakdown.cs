namespace costats.Core.Analytics;

/// <summary>A column the breakdown table can sort by.</summary>
public enum UsageSortColumn
{
    /// <summary>Model id, or the day or hour, by time.</summary>
    Label,

    /// <summary>API list-price estimate.</summary>
    Value,

    /// <summary>Share of the period's estimate; orders like <see cref="Value"/>.</summary>
    Share,

    /// <summary>Processed tokens.</summary>
    Tokens,

    /// <summary>Change against the comparison period.</summary>
    Change,

    /// <summary>API list-price estimate in the comparison period.</summary>
    PreviousValue,

    /// <summary>Processed tokens in the comparison period.</summary>
    PreviousTokens
}

/// <summary>How the breakdown table is sorted.</summary>
public readonly record struct UsageSortState(UsageSortColumn Column, bool Descending)
{
    /// <summary>The model view's default: most expensive first.</summary>
    public static UsageSortState ModelDefault => new(UsageSortColumn.Value, true);

    /// <summary>The day or hour view's default: newest first.</summary>
    public static UsageSortState TimeDefault => new(UsageSortColumn.Label, true);

    /// <summary>
    /// The state after a header click: the same column flips direction, a new
    /// column starts descending, except text labels, which start A to Z.
    /// </summary>
    public UsageSortState Toggle(UsageSortColumn column, bool labelIsTime) =>
        column == Column
            ? this with { Descending = !Descending }
            : new UsageSortState(column, column != UsageSortColumn.Label || labelIsTime);
}

/// <summary>
/// One row of the breakdown table, with the raw values it sorts by.
/// </summary>
public sealed record UsageBreakdownItem
{
    /// <summary>Model id, or the formatted day or hour.</summary>
    public required string Label { get; init; }

    /// <summary>Which agent used the model; null for day and hour rows.</summary>
    public UsageProviderKind? Provider { get; init; }

    /// <summary>Start of the day or hour, the label's sort key; null for model rows.</summary>
    public DateTimeOffset? Time { get; init; }

    /// <summary>The local day a day row stands for, so it can be opened by hour.</summary>
    public DateOnly? Day { get; init; }

    /// <summary>Summed usage of the row in the current period.</summary>
    public UsageTotals Totals { get; init; } = UsageTotals.Empty;

    /// <summary>True when the value column reads "unpriced" rather than a cost.</summary>
    public bool IsUnpriced { get; init; }

    /// <summary>Change against the comparison period, or <see cref="UsageDelta.None"/>.</summary>
    public UsageDelta Delta { get; init; }

    /// <summary>The row's usage in the comparison period, or null when not comparing.</summary>
    public UsageTotals? PreviousTotals { get; init; }

    /// <summary>True when the comparison value reads "unpriced" rather than a cost.</summary>
    public bool IsPreviousUnpriced { get; init; }
}

/// <summary>
/// Builds and sorts the breakdown table's rows. Pure, so the ordering rules
/// are testable without a window.
/// </summary>
public static class UsageBreakdown
{
    /// <summary>
    /// One row per (provider, model) in either period. A model only the
    /// comparison period used is kept with zero current usage, so a model that
    /// stopped being used shows as a 100% drop instead of vanishing.
    /// </summary>
    /// <param name="current">The current period's report.</param>
    /// <param name="previous">The comparison report, or null when not comparing.</param>
    /// <param name="direction">
    /// Which way changes run and how lengths are scaled; the plain "against
    /// the period before" when null.
    /// </param>
    public static IReadOnlyList<UsageBreakdownItem> Models(UsageReport current, UsageReport? previous, UsageChangeDirection? direction = null)
    {
        var change = direction ?? UsageChangeDirection.Default;
        ArgumentNullException.ThrowIfNull(current);

        var rows = new Dictionary<(UsageProviderKind, string), (ModelUsage? Current, ModelUsage? Previous)>();
        foreach (var model in current.ByModel)
        {
            rows[(model.Provider, model.Model)] = (model, null);
        }

        foreach (var model in previous?.ByModel ?? [])
        {
            var key = (model.Provider, model.Model);
            rows[key] = rows.TryGetValue(key, out var existing) ? (existing.Current, model) : (null, model);
        }

        return rows
            .Select(entry =>
            {
                var (now, before) = entry.Value;
                var reference = now ?? before!;
                var totals = now?.Totals ?? UsageTotals.Empty;
                return new UsageBreakdownItem
                {
                    Label = reference.Model,
                    Provider = reference.Provider,
                    Totals = totals,
                    // Each period's "unpriced" comes from its own row: a model
                    // only B used has nothing in A, which is $0.00, not unpriced.
                    IsUnpriced = now is { IsPriced: false },
                    PreviousTotals = previous is null ? null : before?.Totals ?? UsageTotals.Empty,
                    IsPreviousUnpriced = before is { IsPriced: false },
                    Delta = previous is null
                        ? UsageDelta.None
                        : change.Between(totals, before?.Totals ?? UsageTotals.Empty)
                };
            })
            .ToList();
    }

    /// <summary>One row per local day that has usage, with the year on rows outside <paramref name="today"/>'s.</summary>
    public static IReadOnlyList<UsageBreakdownItem> Days(UsageReport report, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Daily
            .Select(day => new UsageBreakdownItem
            {
                Label = UsageNumberFormat.LongDayLabel(day.Day, today),
                Time = new DateTimeOffset(day.Day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                Day = day.Day,
                Totals = day.Totals,
                IsUnpriced = IsUnpriced(day.Totals)
            })
            .ToList();
    }

    /// <summary>
    /// One row per hour that has usage, labelled in <paramref name="zone"/>,
    /// with the year on rows outside <paramref name="today"/>'s.
    /// </summary>
    public static IReadOnlyList<UsageBreakdownItem> Hours(UsageReport report, TimeZoneInfo zone, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(zone);
        return report.Hourly
            .Select(hour => new UsageBreakdownItem
            {
                Label = UsageNumberFormat.LongHourLabel(hour.HourStart, zone, today),
                Time = hour.HourStart,
                Totals = hour.Totals,
                IsUnpriced = IsUnpriced(hour.Totals)
            })
            .ToList();
    }

    /// <summary>
    /// Orders rows by raw values, never by their formatted text.
    /// </summary>
    /// <remarks>
    /// Unpriced rows always go last when sorting by value or share, in either
    /// direction: they have no cost to rank. A row with no change to show goes
    /// last when sorting by change, and "no prior data" ranks above any finite
    /// rise. Ties fall back to the label, then the provider, so the order is
    /// stable across refreshes.
    /// </remarks>
    public static IReadOnlyList<UsageBreakdownItem> Sort(IEnumerable<UsageBreakdownItem> items, UsageSortState sort)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToList();
        list.Sort((left, right) => Compare(left, right, sort));
        return list;
    }

    private static int Compare(UsageBreakdownItem left, UsageBreakdownItem right, UsageSortState sort)
    {
        var lastGroup = LastGroup(left, sort.Column).CompareTo(LastGroup(right, sort.Column));
        if (lastGroup != 0)
        {
            return lastGroup;
        }

        var primary = sort.Column switch
        {
            UsageSortColumn.Label => CompareLabels(left, right),
            UsageSortColumn.Value or UsageSortColumn.Share => left.Totals.CostUsd.CompareTo(right.Totals.CostUsd),
            UsageSortColumn.Tokens => left.Totals.Tokens.ProcessedTokens.CompareTo(right.Totals.Tokens.ProcessedTokens),
            UsageSortColumn.Change => ChangeRank(left.Delta).CompareTo(ChangeRank(right.Delta)),
            UsageSortColumn.PreviousValue => PreviousCost(left).CompareTo(PreviousCost(right)),
            UsageSortColumn.PreviousTokens => PreviousTokens(left).CompareTo(PreviousTokens(right)),
            _ => 0
        };

        if (primary != 0)
        {
            return sort.Descending ? -primary : primary;
        }

        var label = string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);
        if (label != 0)
        {
            return label;
        }

        return Nullable.Compare(left.Provider, right.Provider);
    }

    /// <summary>1 for rows that sort after everything else in this column.</summary>
    private static int LastGroup(UsageBreakdownItem item, UsageSortColumn column) => column switch
    {
        UsageSortColumn.Value or UsageSortColumn.Share => item.IsUnpriced ? 1 : 0,
        UsageSortColumn.PreviousValue => item.IsPreviousUnpriced ? 1 : 0,
        UsageSortColumn.Change => item.Delta.Kind == UsageDeltaKind.None ? 1 : 0,
        _ => 0
    };

    private static decimal PreviousCost(UsageBreakdownItem item) => item.PreviousTotals?.CostUsd ?? 0m;

    private static long PreviousTokens(UsageBreakdownItem item) => item.PreviousTotals?.Tokens.ProcessedTokens ?? 0;

    private static double ChangeRank(UsageDelta delta) => delta.Kind switch
    {
        UsageDeltaKind.NoPriorData => double.PositiveInfinity,
        UsageDeltaKind.Change => delta.Fraction,
        _ => double.NegativeInfinity
    };

    private static int CompareLabels(UsageBreakdownItem left, UsageBreakdownItem right) =>
        left.Time is { } leftTime && right.Time is { } rightTime
            ? leftTime.CompareTo(rightTime)
            : string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnpriced(UsageTotals totals) =>
        totals.CostUsd == 0m && totals.UnpricedTokens > 0;
}
