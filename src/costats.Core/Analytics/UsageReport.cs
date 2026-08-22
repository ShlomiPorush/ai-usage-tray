namespace costats.Core.Analytics;

/// <summary>
/// An inclusive range of <em>local</em> calendar days. A null bound means
/// "unbounded on that side"; <see cref="All"/> is unbounded both ways.
/// </summary>
/// <param name="From">First day to include, or null for no lower bound.</param>
/// <param name="To">Last day to include, or null for no upper bound.</param>
public readonly record struct UsageDateRange(DateOnly? From, DateOnly? To)
{
    /// <summary>Every day the logs contain.</summary>
    public static UsageDateRange All => default;

    /// <summary>
    /// The last <paramref name="days"/> days ending on <paramref name="today"/>,
    /// inclusive: <c>LastDays(1, today)</c> is today only.
    /// </summary>
    public static UsageDateRange LastDays(int days, DateOnly today)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        return new UsageDateRange(today.AddDays(-(days - 1)), today);
    }

    /// <summary>Just one day.</summary>
    public static UsageDateRange Day(DateOnly day) => new(day, day);

    /// <summary>True when <paramref name="day"/> falls inside the range.</summary>
    public bool Contains(DateOnly day) =>
        (From is not { } from || day >= from) &&
        (To is not { } to || day <= to);
}

/// <summary>
/// Tokens, money and request count for one bucket of the report.
/// </summary>
public sealed record UsageTotals
{
    /// <summary>An empty bucket.</summary>
    public static readonly UsageTotals Empty = new();

    /// <summary>Summed token counts.</summary>
    public UsageTokens Tokens { get; init; }

    /// <summary>Raw API list-price cost of <see cref="Tokens"/>, in USD.</summary>
    public decimal CostUsd { get; init; }

    /// <summary>What prompt caching saved against full input rate, in USD.</summary>
    public decimal CacheSavingsUsd { get; init; }

    /// <summary>Deduplicated requests folded into this bucket.</summary>
    public long RequestCount { get; init; }

    /// <summary>
    /// Tokens in this bucket whose model has no known price. Non-zero means
    /// <see cref="CostUsd"/> understates the real cost.
    /// </summary>
    public long UnpricedTokens { get; init; }

    /// <summary>Component-wise sum.</summary>
    public UsageTotals Add(UsageTotals other) => new()
    {
        Tokens = Tokens + other.Tokens,
        CostUsd = CostUsd + other.CostUsd,
        CacheSavingsUsd = CacheSavingsUsd + other.CacheSavingsUsd,
        RequestCount = RequestCount + other.RequestCount,
        UnpricedTokens = UnpricedTokens + other.UnpricedTokens
    };
}

/// <summary>One local day of the time series.</summary>
public sealed record DailyUsage(DateOnly Day, UsageTotals Totals);

/// <summary>One local day of one model, the finest grain the report exposes.</summary>
public sealed record DailyModelUsage(
    DateOnly Day,
    UsageProviderKind Provider,
    string Model,
    UsageTotals Totals);

/// <summary>All usage of one model over the whole range.</summary>
/// <param name="Model">Model id as the log reported it.</param>
/// <param name="Provider">Which agent used it.</param>
/// <param name="IsPriced">False when the pricing table has no rates for it.</param>
/// <param name="Totals">Summed usage.</param>
public sealed record ModelUsage(
    string Model,
    UsageProviderKind Provider,
    bool IsPriced,
    UsageTotals Totals);

/// <summary>All usage of one provider over the whole range.</summary>
public sealed record ProviderUsage(UsageProviderKind Provider, UsageTotals Totals);

/// <summary>All usage of one account over the whole range.</summary>
public sealed record AccountUsage(
    string AccountId,
    UsageProviderKind Provider,
    UsageTotals Totals);

/// <summary>
/// What the scan itself did. Useful for a "last refreshed" line and for
/// spotting a log format change: a sudden jump in
/// <see cref="SkippedLines"/> means the parser stopped understanding something.
/// </summary>
public sealed record UsageScanDiagnostics
{
    /// <summary>Nothing scanned.</summary>
    public static readonly UsageScanDiagnostics Empty = new();

    /// <summary>Distinct log roots scanned after symlink resolution and dedup.</summary>
    public int RootsScanned { get; init; }

    /// <summary>Log files considered.</summary>
    public int FilesSeen { get; init; }

    /// <summary>Files read from disk and parsed this scan.</summary>
    public int FilesParsed { get; init; }

    /// <summary>Files served from the per-file parse cache.</summary>
    public int FilesFromCache { get; init; }

    /// <summary>Files that could not be opened or read at all.</summary>
    public int FilesFailed { get; init; }

    /// <summary>Bytes actually read and parsed this scan.</summary>
    public long BytesParsed { get; init; }

    /// <summary>Lines that looked like usage but could not be parsed.</summary>
    public int SkippedLines { get; init; }

    /// <summary>
    /// Usage entries dropped because the same (message id, request id) pair had
    /// already been counted. Claude Code copies history into every resumed
    /// session file, so this is normally a large fraction of all entries.
    /// </summary>
    public int DuplicatesDropped { get; init; }

    /// <summary>Wall-clock time of the scan.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// The finished analytics answer for one range and account filter. Everything a
/// UI needs is precomputed here; nothing has to be re-derived from samples.
/// </summary>
public sealed record UsageReport
{
    /// <summary>A report over no data.</summary>
    public static readonly UsageReport Empty = new();

    /// <summary>The range that was asked for.</summary>
    public UsageDateRange Range { get; init; }

    /// <summary>The time zone whose calendar days the buckets use.</summary>
    public string TimeZoneId { get; init; } = TimeZoneInfo.Utc.Id;

    /// <summary>The accounts that were included, or empty when unfiltered.</summary>
    public IReadOnlyList<string> AccountFilter { get; init; } = [];

    /// <summary>Earliest local day with data, or null when there is none.</summary>
    public DateOnly? FirstDay { get; init; }

    /// <summary>Latest local day with data, or null when there is none.</summary>
    public DateOnly? LastDay { get; init; }

    /// <summary>
    /// One entry per local day that has data, ascending. Days with no usage are
    /// omitted; a chart that wants a continuous axis should fill the gaps.
    /// </summary>
    public IReadOnlyList<DailyUsage> Daily { get; init; } = [];

    /// <summary>Per day and model, ascending by day then model.</summary>
    public IReadOnlyList<DailyModelUsage> DailyByModel { get; init; } = [];

    /// <summary>Per model over the whole range, most expensive first.</summary>
    public IReadOnlyList<ModelUsage> ByModel { get; init; } = [];

    /// <summary>Per provider over the whole range.</summary>
    public IReadOnlyList<ProviderUsage> ByProvider { get; init; } = [];

    /// <summary>Per account over the whole range.</summary>
    public IReadOnlyList<AccountUsage> ByAccount { get; init; } = [];

    /// <summary>Everything, summed.</summary>
    public UsageTotals Totals { get; init; } = UsageTotals.Empty;

    /// <summary>
    /// Model ids seen in the range that the pricing table cannot price, sorted.
    /// Their tokens are in every total; their cost is not.
    /// </summary>
    public IReadOnlyList<string> UnpricedModels { get; init; } = [];

    /// <summary>How the underlying scan went.</summary>
    public UsageScanDiagnostics Diagnostics { get; init; } = UsageScanDiagnostics.Empty;

    /// <summary>When the report was produced.</summary>
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>True when no request fell inside the range and filter.</summary>
    public bool IsEmpty => Totals.RequestCount == 0;
}
