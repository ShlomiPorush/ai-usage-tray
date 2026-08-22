namespace costats.Core.Analytics;

/// <summary>
/// Knobs for one aggregation pass. Everything has a safe default, so
/// <c>new UsageAggregationOptions()</c> means "all accounts, all days, built-in
/// prices, this machine's time zone".
/// </summary>
public sealed record UsageAggregationOptions
{
    /// <summary>Local days to include.</summary>
    public UsageDateRange Range { get; init; } = UsageDateRange.All;

    /// <summary>
    /// Account ids to include. Null or empty means every account. Matching is
    /// case-insensitive.
    /// </summary>
    public IReadOnlyCollection<string>? AccountIds { get; init; }

    /// <summary>Prices used to cost every bucket.</summary>
    public ModelPricingTable Pricing { get; init; } = ModelPricingTable.Default;

    /// <summary>
    /// The zone whose calendar days form the buckets. Log timestamps are UTC;
    /// people think in local days, so the default is
    /// <see cref="TimeZoneInfo.Local"/>.
    /// </summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>Clock reading stamped on the report.</summary>
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Folds deduplicated <see cref="UsageSample"/>s into a <see cref="UsageReport"/>.
/// Pure: no IO, no clock beyond
/// <see cref="UsageAggregationOptions.GeneratedAt"/>, deterministic for a given
/// input.
/// </summary>
public static class UsageAggregator
{
    /// <summary>
    /// Buckets, prices and totals the samples.
    /// <para>
    /// Each sample is placed on the calendar day its UTC timestamp falls on in
    /// <see cref="UsageAggregationOptions.TimeZone"/>, then filtered by range
    /// and account, then costed with
    /// <see cref="UsageCostCalculator.Compute(UsageTokens, ModelPrice)"/>.
    /// </para>
    /// <para>
    /// Cost is summed per (day, model) bucket rather than per sample so that
    /// rounding cannot drift: a bucket's cost is computed once from its summed
    /// tokens, and every coarser total is the sum of those bucket costs.
    /// </para>
    /// </summary>
    public static UsageReport Aggregate(
        IEnumerable<UsageSample> samples,
        UsageAggregationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var opts = options ?? new UsageAggregationOptions();
        var pricing = opts.Pricing ?? ModelPricingTable.Default;
        var zone = opts.TimeZone ?? TimeZoneInfo.Local;

        var accountFilter = opts.AccountIds is { Count: > 0 }
            ? new HashSet<string>(opts.AccountIds, StringComparer.OrdinalIgnoreCase)
            : null;

        // (day, provider, model) -> tokens, and (day, provider, model, account)
        // is folded straight into the account rollup to keep one pass.
        var byDayModel = new Dictionary<DayModelKey, Accumulator>();
        var byAccountTokens = new Dictionary<AccountKey, Accumulator>();

        foreach (var sample in samples)
        {
            if (sample is null)
            {
                continue;
            }

            if (accountFilter is not null && !accountFilter.Contains(sample.AccountId))
            {
                continue;
            }

            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(sample.Timestamp, zone).DateTime);
            if (!opts.Range.Contains(day))
            {
                continue;
            }

            var model = string.IsNullOrWhiteSpace(sample.Model) ? "unknown" : sample.Model;

            Add(byDayModel, new DayModelKey(day, sample.Provider, model), sample.Tokens);
            Add(byAccountTokens, new AccountKey(sample.AccountId, sample.Provider, model), sample.Tokens);
        }

        var dailyByModel = new List<DailyModelUsage>(byDayModel.Count);
        var perDay = new Dictionary<DateOnly, UsageTotals>();
        var perModel = new Dictionary<ModelKey, UsageTotals>();
        var perProvider = new Dictionary<UsageProviderKind, UsageTotals>();
        var unpriced = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var grand = UsageTotals.Empty;

        foreach (var (key, accumulator) in byDayModel)
        {
            var price = pricing.Find(key.Model);
            var totals = Cost(accumulator, price);
            if (!price.IsPriced)
            {
                unpriced.Add(key.Model);
            }

            dailyByModel.Add(new DailyModelUsage(key.Day, key.Provider, key.Model, totals));

            perDay[key.Day] = perDay.TryGetValue(key.Day, out var day) ? day.Add(totals) : totals;

            var modelKey = new ModelKey(key.Provider, key.Model);
            perModel[modelKey] = perModel.TryGetValue(modelKey, out var model) ? model.Add(totals) : totals;

            perProvider[key.Provider] = perProvider.TryGetValue(key.Provider, out var provider)
                ? provider.Add(totals)
                : totals;

            grand = grand.Add(totals);
        }

        var perAccount = new Dictionary<AccountRollupKey, UsageTotals>();
        foreach (var (key, accumulator) in byAccountTokens)
        {
            var totals = Cost(accumulator, pricing.Find(key.Model));
            var rollup = new AccountRollupKey(key.AccountId, key.Provider);
            perAccount[rollup] = perAccount.TryGetValue(rollup, out var existing) ? existing.Add(totals) : totals;
        }

        dailyByModel.Sort(static (left, right) =>
        {
            var byDay = left.Day.CompareTo(right.Day);
            return byDay != 0
                ? byDay
                : string.Compare(left.Model, right.Model, StringComparison.OrdinalIgnoreCase);
        });

        var daily = perDay
            .OrderBy(entry => entry.Key)
            .Select(entry => new DailyUsage(entry.Key, entry.Value))
            .ToList();

        return new UsageReport
        {
            Range = opts.Range,
            TimeZoneId = zone.Id,
            AccountFilter = accountFilter is null ? [] : [.. accountFilter.OrderBy(id => id, StringComparer.OrdinalIgnoreCase)],
            FirstDay = daily.Count > 0 ? daily[0].Day : null,
            LastDay = daily.Count > 0 ? daily[^1].Day : null,
            Daily = daily,
            DailyByModel = dailyByModel,
            ByModel = perModel
                .Select(entry => new ModelUsage(
                    entry.Key.Model,
                    entry.Key.Provider,
                    pricing.Find(entry.Key.Model).IsPriced,
                    entry.Value))
                .OrderByDescending(model => model.Totals.CostUsd)
                .ThenByDescending(model => model.Totals.Tokens.ProcessedTokens)
                .ThenBy(model => model.Model, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ByProvider = perProvider
                .Select(entry => new ProviderUsage(entry.Key, entry.Value))
                .OrderBy(provider => provider.Provider)
                .ToList(),
            ByAccount = perAccount
                .Select(entry => new AccountUsage(entry.Key.AccountId, entry.Key.Provider, entry.Value))
                .OrderBy(account => account.Provider)
                .ThenBy(account => account.AccountId, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Totals = grand,
            UnpricedModels = [.. unpriced],
            GeneratedAt = opts.GeneratedAt
        };
    }

    private static void Add<TKey>(Dictionary<TKey, Accumulator> map, TKey key, UsageTokens tokens)
        where TKey : notnull
    {
        if (map.TryGetValue(key, out var existing))
        {
            map[key] = new Accumulator(existing.Tokens + tokens, existing.Requests + 1);
        }
        else
        {
            map[key] = new Accumulator(tokens, 1);
        }
    }

    private static UsageTotals Cost(Accumulator accumulator, ModelPrice price)
    {
        var cost = UsageCostCalculator.Compute(accumulator.Tokens, price);
        return new UsageTotals
        {
            Tokens = accumulator.Tokens,
            CostUsd = cost.CostUsd,
            CacheSavingsUsd = cost.CacheSavingsUsd,
            RequestCount = accumulator.Requests,
            UnpricedTokens = cost.IsPriced ? 0 : accumulator.Tokens.ProcessedTokens
        };
    }

    private readonly record struct Accumulator(UsageTokens Tokens, long Requests);

    private readonly record struct DayModelKey(DateOnly Day, UsageProviderKind Provider, string Model);

    private readonly record struct ModelKey(UsageProviderKind Provider, string Model);

    private readonly record struct AccountKey(string AccountId, UsageProviderKind Provider, string Model);

    private readonly record struct AccountRollupKey(string AccountId, UsageProviderKind Provider);
}
