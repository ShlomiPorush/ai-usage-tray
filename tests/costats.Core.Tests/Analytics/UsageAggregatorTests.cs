using costats.Core.Analytics;
using Xunit;

namespace costats.Core.Tests.Analytics;

public sealed class UsageAggregatorTests
{
    private static readonly TimeZoneInfo PlusThree = TimeZoneInfo.CreateCustomTimeZone(
        "Test+3", TimeSpan.FromHours(3), "Test+3", "Test+3");

    private static readonly TimeZoneInfo MinusEight = TimeZoneInfo.CreateCustomTimeZone(
        "Test-8", TimeSpan.FromHours(-8), "Test-8", "Test-8");

    private static readonly ModelPricingTable Pricing = new(
    [
        new KeyValuePair<string, ModelPrice>("test-priced", ModelPrice.Anthropic(10m, 50m))
    ]);

    private static UsageSample Sample(
        string timestamp,
        string model = "test-priced",
        string account = "claude-1",
        UsageProviderKind provider = UsageProviderKind.Claude,
        long uncached = 0,
        long cacheRead = 0,
        long write5m = 0,
        long write1h = 0,
        long output = 0,
        long reasoning = 0) =>
        new(
            DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
            provider,
            account,
            model,
            new UsageTokens
            {
                UncachedInputTokens = uncached,
                CacheReadInputTokens = cacheRead,
                CacheWrite5mInputTokens = write5m,
                CacheWrite1hInputTokens = write1h,
                OutputTokens = output,
                ReasoningOutputTokens = reasoning
            });

    private static UsageAggregationOptions Options(
        UsageDateRange? range = null,
        IReadOnlyCollection<string>? accounts = null,
        TimeZoneInfo? zone = null) => new()
        {
            Range = range ?? UsageDateRange.All,
            AccountIds = accounts,
            Pricing = Pricing,
            TimeZone = zone ?? TimeZoneInfo.Utc
        };

    [Fact]
    public void Aggregate_sums_every_token_bucket_and_counts_requests()
    {
        var report = UsageAggregator.Aggregate(
            [
                Sample("2026-08-01T10:00:00Z", uncached: 100, cacheRead: 1_000, write5m: 200, write1h: 50, output: 300, reasoning: 120),
                Sample("2026-08-01T11:00:00Z", uncached: 5, cacheRead: 2_000, write5m: 0, write1h: 10, output: 7, reasoning: 3)
            ],
            Options());

        var tokens = report.Totals.Tokens;
        Assert.Equal(105, tokens.UncachedInputTokens);
        Assert.Equal(3_000, tokens.CacheReadInputTokens);
        Assert.Equal(200, tokens.CacheWrite5mInputTokens);
        Assert.Equal(60, tokens.CacheWrite1hInputTokens);
        Assert.Equal(260, tokens.CacheWriteInputTokens);
        Assert.Equal(307, tokens.OutputTokens);
        Assert.Equal(123, tokens.ReasoningOutputTokens);
        Assert.Equal(3_365, tokens.InputTokens);
        Assert.Equal(3_672, tokens.ProcessedTokens);
        Assert.Equal(2, report.Totals.RequestCount);
    }

    [Fact]
    public void Reasoning_tokens_are_reported_but_never_added_to_the_total()
    {
        // Reasoning is already inside output_tokens; counting it again would
        // inflate both the token total and the cost.
        var report = UsageAggregator.Aggregate(
            [Sample("2026-08-01T10:00:00Z", output: 1_000, reasoning: 900)],
            Options());

        Assert.Equal(1_000, report.Totals.Tokens.ProcessedTokens);
        Assert.Equal(900, report.Totals.Tokens.ReasoningOutputTokens);
        Assert.Equal(0.05m, report.Totals.CostUsd); // 1000 output at $50 / MTok
    }

    [Fact]
    public void Days_are_local_calendar_days_not_utc_days()
    {
        // 22:30 UTC is already the next day at UTC+3 and still the same day at UTC.
        var samples = new[] { Sample("2026-08-01T22:30:00Z", output: 10) };

        var utc = UsageAggregator.Aggregate(samples, Options(zone: TimeZoneInfo.Utc));
        var plusThree = UsageAggregator.Aggregate(samples, Options(zone: PlusThree));

        Assert.Equal(new DateOnly(2026, 8, 1), Assert.Single(utc.Daily).Day);
        Assert.Equal(new DateOnly(2026, 8, 2), Assert.Single(plusThree.Daily).Day);
    }

    [Fact]
    public void Days_roll_backwards_in_a_negative_offset_zone()
    {
        // 03:00 UTC is still the previous evening at UTC-8.
        var samples = new[] { Sample("2026-08-02T03:00:00Z", output: 10) };

        var report = UsageAggregator.Aggregate(samples, Options(zone: MinusEight));

        Assert.Equal(new DateOnly(2026, 8, 1), Assert.Single(report.Daily).Day);
        Assert.Equal("Test-8", report.TimeZoneId);
    }

    [Fact]
    public void Two_samples_either_side_of_local_midnight_land_on_different_days()
    {
        var report = UsageAggregator.Aggregate(
            [
                Sample("2026-08-01T20:59:00Z", output: 1),
                Sample("2026-08-01T21:01:00Z", output: 2)
            ],
            Options(zone: PlusThree));

        Assert.Equal(2, report.Daily.Count);
        Assert.Equal(new DateOnly(2026, 8, 1), report.Daily[0].Day);
        Assert.Equal(1, report.Daily[0].Totals.Tokens.OutputTokens);
        Assert.Equal(new DateOnly(2026, 8, 2), report.Daily[1].Day);
        Assert.Equal(2, report.Daily[1].Totals.Tokens.OutputTokens);
        Assert.Equal(new DateOnly(2026, 8, 1), report.FirstDay);
        Assert.Equal(new DateOnly(2026, 8, 2), report.LastDay);
    }

    [Fact]
    public void Range_is_applied_to_local_days_so_the_zone_can_move_a_sample_out_of_it()
    {
        var samples = new[] { Sample("2026-08-01T22:30:00Z", output: 10) };
        var range = UsageDateRange.Day(new DateOnly(2026, 8, 1));

        Assert.Equal(10, UsageAggregator.Aggregate(samples, Options(range, zone: TimeZoneInfo.Utc)).Totals.Tokens.OutputTokens);
        Assert.True(UsageAggregator.Aggregate(samples, Options(range, zone: PlusThree)).IsEmpty);
    }

    [Fact]
    public void Range_bounds_are_inclusive_and_a_null_bound_is_open()
    {
        var samples = new[]
        {
            Sample("2026-08-01T10:00:00Z", output: 1),
            Sample("2026-08-02T10:00:00Z", output: 2),
            Sample("2026-08-03T10:00:00Z", output: 4)
        };

        var middle = UsageAggregator.Aggregate(
            samples,
            Options(new UsageDateRange(new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 2))));
        Assert.Equal(2, middle.Totals.Tokens.OutputTokens);

        var openStart = UsageAggregator.Aggregate(
            samples,
            Options(new UsageDateRange(null, new DateOnly(2026, 8, 2))));
        Assert.Equal(3, openStart.Totals.Tokens.OutputTokens);

        var openEnd = UsageAggregator.Aggregate(
            samples,
            Options(new UsageDateRange(new DateOnly(2026, 8, 2), null)));
        Assert.Equal(6, openEnd.Totals.Tokens.OutputTokens);
    }

    [Fact]
    public void LastDays_covers_today_back_through_the_requested_count()
    {
        var today = new DateOnly(2026, 8, 10);

        Assert.Equal(new UsageDateRange(today, today), UsageDateRange.LastDays(1, today));
        Assert.Equal(new UsageDateRange(new DateOnly(2026, 8, 4), today), UsageDateRange.LastDays(7, today));
        Assert.Throws<ArgumentOutOfRangeException>(() => UsageDateRange.LastDays(0, today));
    }

    [Fact]
    public void Account_filter_keeps_only_the_requested_accounts()
    {
        var samples = new[]
        {
            Sample("2026-08-01T10:00:00Z", account: "claude-1", output: 1),
            Sample("2026-08-01T10:00:00Z", account: "claude-2", output: 2),
            Sample("2026-08-01T10:00:00Z", account: "codex", provider: UsageProviderKind.Codex, output: 4)
        };

        var all = UsageAggregator.Aggregate(samples, Options());
        Assert.Equal(7, all.Totals.Tokens.OutputTokens);
        Assert.Equal(3, all.ByAccount.Count);
        Assert.Empty(all.AccountFilter);

        var filtered = UsageAggregator.Aggregate(samples, Options(accounts: ["claude-2", "codex"]));
        Assert.Equal(6, filtered.Totals.Tokens.OutputTokens);
        Assert.Equal(["claude-2", "codex"], filtered.AccountFilter);
        Assert.Equal(2, filtered.ByAccount.Count);
    }

    [Fact]
    public void Account_filter_is_case_insensitive()
    {
        var report = UsageAggregator.Aggregate(
            [Sample("2026-08-01T10:00:00Z", account: "Claude-1", output: 5)],
            Options(accounts: ["claude-1"]));

        Assert.Equal(5, report.Totals.Tokens.OutputTokens);
    }

    [Fact]
    public void An_empty_account_filter_means_every_account()
    {
        var report = UsageAggregator.Aggregate(
            [Sample("2026-08-01T10:00:00Z", account: "anything", output: 5)],
            Options(accounts: []));

        Assert.Equal(5, report.Totals.Tokens.OutputTokens);
    }

    [Fact]
    public void Report_splits_by_day_model_provider_and_account()
    {
        var report = UsageAggregator.Aggregate(
            [
                Sample("2026-08-01T10:00:00Z", model: "test-priced", account: "claude-1", output: 1),
                Sample("2026-08-01T11:00:00Z", model: "other", account: "claude-1", output: 2),
                Sample("2026-08-02T10:00:00Z", model: "test-priced", account: "codex", provider: UsageProviderKind.Codex, output: 4)
            ],
            Options());

        Assert.Equal(2, report.Daily.Count);
        Assert.Equal(3, report.DailyByModel.Count);
        // ByModel is keyed by provider as well, so the same model id used by two
        // providers stays two rows rather than being silently merged.
        Assert.Equal(3, report.ByModel.Count);
        Assert.Equal(2, report.ByModel.Count(entry => entry.Model == "test-priced"));
        Assert.Equal(2, report.ByProvider.Count);
        Assert.Equal(2, report.ByAccount.Count);

        Assert.Equal(
            [new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 2)],
            report.DailyByModel.Select(entry => entry.Day));

        var claude = Assert.Single(report.ByProvider, entry => entry.Provider == UsageProviderKind.Claude);
        Assert.Equal(3, claude.Totals.Tokens.OutputTokens);
        Assert.Equal(2, claude.Totals.RequestCount);
    }

    [Fact]
    public void Every_rollup_sums_back_to_the_grand_total()
    {
        var samples = Enumerable.Range(0, 24).Select(index => Sample(
            $"2026-08-{(index % 3) + 1:00}T{index:00}:00:00Z",
            model: index % 2 == 0 ? "test-priced" : "other",
            account: index % 4 == 0 ? "claude-1" : "claude-2",
            uncached: index,
            cacheRead: index * 10,
            write5m: index * 2,
            output: index * 3)).ToList();

        var report = UsageAggregator.Aggregate(samples, Options());

        Assert.Equal(report.Totals.CostUsd, report.Daily.Sum(day => day.Totals.CostUsd));
        Assert.Equal(report.Totals.CostUsd, report.ByModel.Sum(model => model.Totals.CostUsd));
        Assert.Equal(report.Totals.CostUsd, report.ByProvider.Sum(provider => provider.Totals.CostUsd));
        Assert.Equal(report.Totals.CostUsd, report.ByAccount.Sum(account => account.Totals.CostUsd));
        Assert.Equal(report.Totals.RequestCount, report.Daily.Sum(day => day.Totals.RequestCount));
        Assert.Equal(report.Totals.RequestCount, report.ByAccount.Sum(account => account.Totals.RequestCount));
        Assert.Equal(
            report.Totals.Tokens.ProcessedTokens,
            report.ByModel.Sum(model => model.Totals.Tokens.ProcessedTokens));
    }

    [Fact]
    public void Aggregating_nothing_produces_an_empty_report()
    {
        var report = UsageAggregator.Aggregate([], Options());

        Assert.True(report.IsEmpty);
        Assert.Empty(report.Daily);
        Assert.Empty(report.ByModel);
        Assert.Null(report.FirstDay);
        Assert.Null(report.LastDay);
        Assert.Equal(0m, report.Totals.CostUsd);
    }
}
