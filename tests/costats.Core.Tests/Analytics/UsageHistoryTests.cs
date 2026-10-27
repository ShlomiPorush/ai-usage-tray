using System.Globalization;
using costats.Core.Analytics;
using Xunit;

namespace costats.Core.Tests.Analytics;

public sealed class UsageHistoryTests
{
    private static readonly TimeZoneInfo Kathmandu = TimeZoneInfo.CreateCustomTimeZone(
        "Test+0545", new TimeSpan(5, 45, 0), "Test+0545", "Test+0545");

    private static readonly ModelPricingTable Pricing = new(
    [
        new KeyValuePair<string, ModelPrice>("test-priced", new ModelPrice { InputPerMTok = 10m, CachedInputPerMTok = 1m, CacheWrite5mPerMTok = 12.5m, CacheWrite1hPerMTok = 20m, OutputPerMTok = 50m })
    ]);

    private static UsageSample Sample(string timestamp, long output = 10, string account = "claude-1", string model = "test-priced") =>
        new(
            DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            UsageProviderKind.Claude,
            account,
            model,
            new UsageTokens { UncachedInputTokens = 100, OutputTokens = output });

    private static string? KeyOf(string accountId) => accountId == "claude-1" ? "claude:k1" : null;

    private static string IdOf(string key) => key == "claude:k1" ? "claude-1" : UsageHistory.ArchivedAccountIdPrefix + key;

    private static readonly Dictionary<UsageHistoryKey, UsageHistoryBucket> NoHistory = [];

    [Theory]
    [InlineData("2026-08-21T12:00:00Z", "2026-08-21T12:00:00Z")]
    [InlineData("2026-08-21T12:14:59Z", "2026-08-21T12:00:00Z")]
    [InlineData("2026-08-21T12:15:00Z", "2026-08-21T12:15:00Z")]
    [InlineData("2026-08-21T23:59:59Z", "2026-08-21T23:45:00Z")]
    public void Buckets_are_fifteen_minute_utc_slots(string instant, string expectedStart)
    {
        var start = UsageHistory.BucketStartOf(DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture));

        Assert.Equal(DateTimeOffset.Parse(expectedStart, CultureInfo.InvariantCulture).ToUnixTimeSeconds(), start);
    }

    [Fact]
    public void Bucketize_sums_tokens_and_requests_and_skips_unknown_accounts_and_older_samples()
    {
        var buckets = UsageHistory.Bucketize(
            [
                Sample("2026-08-21T12:01:00Z", output: 10),
                Sample("2026-08-21T12:14:00Z", output: 20),
                Sample("2026-08-21T12:20:00Z", output: 5),
                Sample("2026-08-21T12:02:00Z", account: "nobody"),
                Sample("2026-08-20T12:00:00Z")
            ],
            KeyOf,
            notBefore: DateTimeOffset.Parse("2026-08-21T00:00:00Z", CultureInfo.InvariantCulture));

        Assert.Equal(2, buckets.Count);
        var first = buckets[new UsageHistoryKey(
            DateTimeOffset.Parse("2026-08-21T12:00:00Z", CultureInfo.InvariantCulture).ToUnixTimeSeconds(),
            UsageProviderKind.Claude, "claude:k1", "test-priced")];
        Assert.Equal(30, first.Tokens.OutputTokens);
        Assert.Equal(200, first.Tokens.UncachedInputTokens);
        Assert.Equal(2, first.Requests);
    }

    [Fact]
    public void Merge_keeps_the_larger_bucket_and_never_adds_both()
    {
        var key = new UsageHistoryKey(0, UsageProviderKind.Claude, "claude:k1", "m");
        var history = new Dictionary<UsageHistoryKey, UsageHistoryBucket>
        {
            [key] = new(new UsageTokens { OutputTokens = 100 }, 3)
        };

        var smaller = UsageHistory.MergeLarger(history, [new(key, new UsageHistoryBucket(new UsageTokens { OutputTokens = 40 }, 1))]);
        Assert.Equal(0, smaller);
        Assert.Equal(100, history[key].Tokens.OutputTokens);

        var larger = UsageHistory.MergeLarger(history, [new(key, new UsageHistoryBucket(new UsageTokens { OutputTokens = 150 }, 4))]);
        Assert.Equal(1, larger);
        Assert.Equal(150, history[key].Tokens.OutputTokens);
        Assert.Equal(4, history[key].Requests);
    }

    [Fact]
    public void Combine_uses_live_samples_while_the_log_is_complete()
    {
        var live = new List<UsageSample> { Sample("2026-08-21T12:01:00Z"), Sample("2026-08-21T12:07:00Z") };
        var history = UsageHistory.Bucketize(live, KeyOf);

        var combined = UsageHistory.Combine(live, KeyOf, history, IdOf, notBefore: null);

        Assert.Equal(live, combined);
    }

    [Fact]
    public void Combine_uses_history_once_the_log_is_gone_and_keeps_the_request_count()
    {
        var original = new List<UsageSample> { Sample("2026-08-21T12:01:00Z"), Sample("2026-08-21T12:07:00Z") };
        var history = UsageHistory.Bucketize(original, KeyOf);

        var combined = UsageHistory.Combine([], KeyOf, history, IdOf, notBefore: null);

        var only = Assert.Single(combined);
        Assert.Equal(DateTimeOffset.Parse("2026-08-21T12:00:00Z", CultureInfo.InvariantCulture), only.Timestamp);
        Assert.Equal("claude-1", only.AccountId);
        Assert.Equal(2, only.Requests);
        Assert.Equal(20, only.Tokens.OutputTokens);
    }

    [Fact]
    public void Combine_prefers_history_when_a_scan_only_read_part_of_a_bucket()
    {
        var original = new List<UsageSample> { Sample("2026-08-21T12:01:00Z"), Sample("2026-08-21T12:07:00Z") };
        var history = UsageHistory.Bucketize(original, KeyOf);

        var combined = UsageHistory.Combine([original[0]], KeyOf, history, IdOf, notBefore: null);

        var only = Assert.Single(combined);
        Assert.Equal(2, only.Requests);
        Assert.Equal(20, only.Tokens.OutputTokens);
    }

    [Fact]
    public void Combine_shows_history_of_a_removed_account_under_an_archived_id()
    {
        var history = UsageHistory.Bucketize([Sample("2026-08-21T12:01:00Z", account: "old")], id => id == "old" ? "claude:gone" : null);

        var combined = UsageHistory.Combine([], KeyOf, history, IdOf, notBefore: null);

        Assert.Equal(UsageHistory.ArchivedAccountIdPrefix + "claude:gone", Assert.Single(combined).AccountId);
    }

    [Fact]
    public void Combine_hides_everything_before_the_deletion_point_from_both_sources()
    {
        var live = new List<UsageSample> { Sample("2026-08-21T12:01:00Z"), Sample("2026-08-22T12:01:00Z") };
        var history = UsageHistory.Bucketize([Sample("2026-08-20T09:00:00Z"), .. live], KeyOf);

        var combined = UsageHistory.Combine(live, KeyOf, history, IdOf,
            notBefore: DateTimeOffset.Parse("2026-08-22T00:00:00Z", CultureInfo.InvariantCulture));

        Assert.Equal(live[1], Assert.Single(combined));
    }

    [Fact]
    public void Usage_saved_right_after_a_delete_survives_its_log_being_deleted()
    {
        // History was deleted at 12:05:01; a request at 12:07 was saved into
        // the 12:00 bucket and its log is gone now.
        var floor = DateTimeOffset.Parse("2026-08-21T12:05:01Z", CultureInfo.InvariantCulture);
        var history = UsageHistory.Bucketize([Sample("2026-08-21T12:07:00Z")], KeyOf, floor);

        var combined = UsageHistory.Combine([], KeyOf, history, IdOf, floor);

        var only = Assert.Single(combined);
        Assert.Equal(floor, only.Timestamp);
        Assert.Equal(1, only.Requests);
    }

    [Fact]
    public void Saved_usage_after_a_delete_wins_over_older_usage_still_in_a_log()
    {
        // Deleted at 12:05:01. Saved after the delete: 100 output tokens whose
        // log is gone. Still in a log: 1,000 from before the delete and 10
        // after it, all in the same 12:00 bucket.
        var floor = DateTimeOffset.Parse("2026-08-21T12:05:01Z", CultureInfo.InvariantCulture);
        var history = UsageHistory.Bucketize([Sample("2026-08-21T12:06:00Z", output: 100)], KeyOf, floor);
        var live = new List<UsageSample>
        {
            Sample("2026-08-21T12:01:00Z", output: 1_000),
            Sample("2026-08-21T12:07:00Z", output: 10)
        };

        var combined = UsageHistory.Combine(live, KeyOf, history, IdOf, floor);

        Assert.Equal(100, Assert.Single(combined).Tokens.OutputTokens);
    }

    [Fact]
    public void A_report_rebuilt_from_history_matches_the_original_in_a_quarter_hour_time_zone()
    {
        // Kathmandu is UTC+5:45, so local midnight is 18:15 UTC: these samples
        // straddle it and must land on the same local days either way.
        var original = new List<UsageSample>
        {
            Sample("2026-08-21T18:10:00Z", output: 7),
            Sample("2026-08-21T18:14:59Z", output: 11),
            Sample("2026-08-21T18:15:00Z", output: 13),
            Sample("2026-08-21T18:29:00Z", output: 17),
            Sample("2026-08-22T03:00:00Z", output: 19)
        };
        var options = new UsageAggregationOptions { Pricing = Pricing, TimeZone = Kathmandu };

        var expected = UsageAggregator.Aggregate(original, options);
        var rebuilt = UsageAggregator.Aggregate(
            UsageHistory.Combine([], KeyOf, UsageHistory.Bucketize(original, KeyOf), IdOf, notBefore: null),
            options);

        Assert.Equal(expected.Daily, rebuilt.Daily);
        Assert.Equal(expected.Totals, rebuilt.Totals);
        Assert.Equal(5, rebuilt.Totals.RequestCount);
        Assert.Equal(expected.ByAccount, rebuilt.ByAccount);
    }

    [Fact]
    public void The_aggregator_counts_a_sample_as_its_request_weight()
    {
        var report = UsageAggregator.Aggregate(
            [Sample("2026-08-21T12:00:00Z") with { Requests = 5 }],
            new UsageAggregationOptions { Pricing = Pricing, TimeZone = TimeZoneInfo.Utc });

        Assert.Equal(5, report.Totals.RequestCount);
        Assert.Equal(5, Assert.Single(report.Daily).Totals.RequestCount);
    }
}
