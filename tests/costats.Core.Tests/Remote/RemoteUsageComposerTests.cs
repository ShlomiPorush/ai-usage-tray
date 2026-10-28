using System.Globalization;
using System.Text;
using System.Text.Json;
using costats.Core.Analytics;
using costats.Core.Remote;
using Xunit;

namespace costats.Core.Tests.Remote;

public sealed class RemoteUsageComposerTests
{
    private static readonly ModelPricingTable Pricing = new(
    [
        Price("claude-opus-test", new ModelPrice { InputPerMTok = 15m, CachedInputPerMTok = 1.5m, CacheWrite5mPerMTok = 18.75m, CacheWrite1hPerMTok = 30m, OutputPerMTok = 75m }),
        Price("gpt-test", new ModelPrice { InputPerMTok = 1.25m, CachedInputPerMTok = 0.125m, OutputPerMTok = 10m }),
        Price("half-micro", new ModelPrice { InputPerMTok = 0.5m, OutputPerMTok = 0.5m }),
        // A user override that charges more for cache reads than for input.
        Price("odd-override", new ModelPrice { InputPerMTok = 1m, CachedInputPerMTok = 3m, OutputPerMTok = 1m })
    ]);

    private static readonly RemoteUsageAccountSource[] DefaultAccounts =
    [
        new("claude-1", UsageProviderKind.Claude, "Work"),
        new("codex", UsageProviderKind.Codex, UsageAccounts.MergedCodexDisplayName)
    ];

    private static KeyValuePair<string, ModelPrice> Price(string model, ModelPrice price) => new(model, price);

    private static TimeZoneInfo Zone(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);

    private static DateTimeOffset Utc(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static UsageSample Sample(
        string utc,
        string account = "claude-1",
        UsageProviderKind provider = UsageProviderKind.Claude,
        string model = "claude-opus-test",
        long uncached = 1000,
        long cacheRead = 0,
        long write5m = 0,
        long write1h = 0,
        long output = 100,
        long reasoning = 0,
        long requests = 1) =>
        new(Utc(utc), provider, account, model, new UsageTokens
        {
            UncachedInputTokens = uncached,
            CacheReadInputTokens = cacheRead,
            CacheWrite5mInputTokens = write5m,
            CacheWrite1hInputTokens = write1h,
            OutputTokens = output,
            ReasoningOutputTokens = reasoning
        })
        { Requests = requests };

    private static RemoteUsagePayload Compose(
        IReadOnlyList<UsageSample> samples,
        string now,
        string zone = "Europe/London",
        IReadOnlyList<RemoteUsageAccountSource>? accounts = null,
        RemoteUsageLimits? limits = null)
    {
        var composition = RemoteUsageComposer.Compose(
            new RemoteUsageSource(samples, Pricing, accounts ?? DefaultAccounts),
            zone,
            Zone(zone),
            Utc(now),
            limits);
        Assert.Null(composition.SkipReason);
        return composition.Payload!;
    }

    private static JsonElement Json(RemoteUsagePayload payload, long seq = 42) =>
        JsonDocument.Parse(payload.ToUtf8Json(seq)).RootElement;

    private static string[] Names(JsonElement element) => [.. element.EnumerateObject().Select(property => property.Name)];

    [Fact]
    public void Payload_has_exactly_the_contract_shape_and_field_names()
    {
        var payload = Compose(
            [
                Sample("2026-10-20T09:00:00Z", reasoning: 20, cacheRead: 500, write5m: 10, write1h: 5),
                Sample("2026-10-28T09:30:00Z", account: "codex", provider: UsageProviderKind.Codex, model: "gpt-test")
            ],
            "2026-10-28T10:00:30.700Z");

        var root = Json(payload, seq: 1761650000123);

        Assert.Equal(
            ["version", "seq", "generatedAt", "timeZone", "coverage", "accounts", "models", "days", "hourDayStarts", "hours", "rolling"],
            Names(root));
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(1761650000123, root.GetProperty("seq").GetInt64());
        Assert.Equal("2026-10-28T10:00:30Z", root.GetProperty("generatedAt").GetString());
        Assert.Equal("Europe/London", root.GetProperty("timeZone").GetString());

        var coverage = root.GetProperty("coverage");
        Assert.Equal(["dailyFrom", "dailyTo", "hourlyFrom", "hourlyTo"], Names(coverage));
        Assert.Equal("2024-10-28", coverage.GetProperty("dailyFrom").GetString());
        Assert.Equal("2026-10-28", coverage.GetProperty("dailyTo").GetString());
        Assert.Equal("2026-10-22", coverage.GetProperty("hourlyFrom").GetString());
        Assert.Equal("2026-10-28", coverage.GetProperty("hourlyTo").GetString());

        foreach (var account in root.GetProperty("accounts").EnumerateArray())
        {
            Assert.Equal(["name", "provider"], Names(account));
        }

        var models = root.GetProperty("models").EnumerateArray().ToList();
        Assert.All(models, model => Assert.Equal(["id", "provider", "priced", "price"], Names(model)));
        var opus = models.Single(model => model.GetProperty("id").GetString() == "claude-opus-test");
        Assert.Equal(["input", "cacheRead", "cacheWrite5m", "cacheWrite1h", "output"], Names(opus.GetProperty("price")));
        Assert.Equal("15", opus.GetProperty("price").GetProperty("input").GetRawText());
        Assert.Equal("18.75", opus.GetProperty("price").GetProperty("cacheWrite5m").GetRawText());
        var gpt = models.Single(model => model.GetProperty("id").GetString() == "gpt-test");
        Assert.Equal(JsonValueKind.Null, gpt.GetProperty("price").GetProperty("cacheWrite5m").ValueKind);

        Assert.All(root.GetProperty("days").EnumerateArray(), row => Assert.Equal(13, row.GetArrayLength()));
        Assert.Equal(8, root.GetProperty("hourDayStarts").GetArrayLength());
        var grid = Assert.Single(root.GetProperty("hours").EnumerateArray());
        Assert.Equal(["phaseMinutes", "rows"], Names(grid));
        Assert.All(grid.GetProperty("rows").EnumerateArray(), row => Assert.Equal(14, row.GetArrayLength()));

        var rolling = root.GetProperty("rolling");
        Assert.Equal(["current", "previous"], Names(rolling));
        Assert.Equal(["since", "until", "rows"], Names(rolling.GetProperty("current")));
        Assert.Equal("2026-10-27T10:00:00Z", rolling.GetProperty("current").GetProperty("since").GetString());
        Assert.Equal("2026-10-28T10:00:00Z", rolling.GetProperty("current").GetProperty("until").GetString());
        Assert.Equal("2026-10-26T10:00:00Z", rolling.GetProperty("previous").GetProperty("since").GetString());
        Assert.Equal("2026-10-27T10:00:00Z", rolling.GetProperty("previous").GetProperty("until").GetString());
        Assert.All(rolling.GetProperty("current").GetProperty("rows").EnumerateArray(), row => Assert.Equal(13, row.GetArrayLength()));
    }

    [Fact]
    public void Rows_reference_accounts_and_models_by_index_and_days_by_offset()
    {
        var payload = Compose(
            [
                Sample("2026-10-20T09:00:00Z", uncached: 1_000_000, output: 0, reasoning: 0, requests: 3),
                Sample("2026-10-21T09:00:00Z", account: "codex", provider: UsageProviderKind.Codex, model: "gpt-test", uncached: 2000, cacheRead: 4000, output: 500, reasoning: 200)
            ],
            "2026-10-28T10:00:00Z");

        var root = Json(payload);
        var accounts = root.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToList();
        var models = root.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToList();
        var dailyFrom = DateOnly.Parse(root.GetProperty("coverage").GetProperty("dailyFrom").GetString()!, CultureInfo.InvariantCulture);
        var rows = root.GetProperty("days").EnumerateArray().Select(row => row.EnumerateArray().Select(value => value.GetInt64()).ToArray()).ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(new DateOnly(2026, 10, 20), dailyFrom.AddDays((int)rows[0][0]));
        Assert.Equal("Work", accounts[(int)rows[0][1]]);
        Assert.Equal("claude-opus-test", models[(int)rows[0][2]]);
        Assert.Equal([1_000_000, 0, 0, 0, 0, 0, 15_000_000, 0, 3, 0], rows[0][3..]);

        Assert.Equal(new DateOnly(2026, 10, 21), dailyFrom.AddDays((int)rows[1][0]));
        Assert.Equal(UsageAccounts.MergedCodexDisplayName, accounts[(int)rows[1][1]]);
        Assert.Equal("gpt-test", models[(int)rows[1][2]]);
        // 2000 x 1.25 + 4000 x 0.125 + 500 x 10 = 8000 micro-dollars; savings 4000 x 1.125 = 4500.
        Assert.Equal([2000, 4000, 0, 0, 500, 200, 8000, 4500, 1, 0], rows[1][3..]);
    }

    [Fact]
    public void Internal_account_ids_never_appear_in_the_json()
    {
        const string archivedNameless = "archived:claude:0123456789abcdef";
        const string archivedNamed = "archived:claude:fedcba9876543210";
        const string unknown = "archived:claude:unknown";
        var payload = Compose(
            [
                Sample("2026-10-20T09:00:00Z", account: "claude-7"),
                Sample("2026-10-20T10:00:00Z", account: archivedNameless),
                Sample("2026-10-20T11:00:00Z", account: archivedNamed),
                Sample("2026-10-20T12:00:00Z", account: unknown),
                Sample("2026-10-20T13:00:00Z", account: "archived:claude:orphan-without-entry"),
                Sample("2026-10-20T14:00:00Z", account: "codex-3", provider: UsageProviderKind.Codex, model: "gpt-test")
            ],
            "2026-10-28T10:00:00Z",
            accounts:
            [
                // Nameless: Settings shows the id as its name.
                new RemoteUsageAccountSource("claude-7", UsageProviderKind.Claude, "claude-7"),
                new RemoteUsageAccountSource(archivedNameless, UsageProviderKind.Claude, null, RemoteUsageAccountKind.Removed),
                new RemoteUsageAccountSource(archivedNamed, UsageProviderKind.Claude, "Old laptop", RemoteUsageAccountKind.Removed),
                new RemoteUsageAccountSource(unknown, UsageProviderKind.Claude, null, RemoteUsageAccountKind.UnknownHistory),
                new RemoteUsageAccountSource("codex", UsageProviderKind.Codex, "Codex (all accounts)")
            ]);

        var json = Encoding.UTF8.GetString(payload.ToUtf8Json(42));

        foreach (var id in new[] { "claude-7", "archived", "0123456789abcdef", "fedcba9876543210", "claude:unknown", "orphan", "codex-3" })
        {
            Assert.DoesNotContain(id, json, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(
            [
                "Claude account 1",
                "Claude account 2 (removed)",
                "Claude account 3 (removed)",
                "Earlier Claude history (account unknown)",
                "Old laptop (removed)",
                "Codex (all accounts)"
            ],
            payload.Accounts.Select(account => account.Name));
        Assert.Equal(["claude", "claude", "claude", "claude", "claude", "codex"], payload.Accounts.Select(account => account.Provider));
    }

    [Fact]
    public void Nameless_claude_accounts_are_numbered_by_position_among_claude_accounts()
    {
        var payload = Compose(
            [Sample("2026-10-20T09:00:00Z")],
            "2026-10-28T10:00:00Z",
            accounts:
            [
                new RemoteUsageAccountSource("claude-1", UsageProviderKind.Claude, "  Zed  "),
                new RemoteUsageAccountSource("claude-2", UsageProviderKind.Claude, ""),
                new RemoteUsageAccountSource("claude-3", UsageProviderKind.Claude, "Alpha"),
                new RemoteUsageAccountSource("claude-4", UsageProviderKind.Claude, new string('n', 80))
            ]);

        Assert.Equal(["Alpha", "Claude account 2", new string('n', 64), "Zed"], payload.Accounts.Select(account => account.Name));
    }

    [Theory]
    [InlineData("claude-2", true)]
    [InlineData("Claude-12", true)]
    [InlineData("codex", true)]
    [InlineData("claude", true)]
    [InlineData("claude-", false)]
    [InlineData("claude-work", false)]
    [InlineData("Work", false)]
    [InlineData("", false)]
    public void Saved_names_that_are_generated_ids_are_recognised(string name, bool expected)
    {
        Assert.Equal(expected, RemoteUsageComposer.LooksLikeAccountId(name));
    }

    [Theory]
    [InlineData("0.0000005", 0)]
    [InlineData("0.0000015", 2)]
    [InlineData("0.0000025", 2)]
    [InlineData("0.0000035", 4)]
    [InlineData("-0.0000025", -2)]
    [InlineData("1.2345675", 1_234_568)]
    [InlineData("1.2345665", 1_234_566)]
    public void Micro_dollars_round_half_to_even(string usd, long expected)
    {
        Assert.Equal(expected, RemoteUsageComposer.ToMicroUsd(decimal.Parse(usd, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Bucket_cost_is_rounded_once_half_to_even()
    {
        // 5 tokens at 0.5 USD per million is 2.5 micro-dollars, and 3 is 1.5.
        var payload = Compose(
            [
                Sample("2026-10-20T09:00:00Z", model: "half-micro", uncached: 3, output: 0),
                Sample("2026-10-21T09:00:00Z", model: "half-micro", uncached: 2, output: 3)
            ],
            "2026-10-28T10:00:00Z");

        Assert.Equal([2L, 2L], payload.Days.Select(row => row.Metrics[6]));
    }

    [Fact]
    public void Savings_stay_signed_when_an_override_makes_them_negative()
    {
        var payload = Compose(
            [Sample("2026-10-20T09:00:00Z", model: "odd-override", uncached: 0, cacheRead: 1_000_000, output: 0)],
            "2026-10-28T10:00:00Z");

        var row = Assert.Single(payload.Days);
        Assert.Equal(3_000_000, row.Metrics[6]);
        Assert.Equal(-2_000_000, row.Metrics[7]);
        Assert.Contains("-2000000", Encoding.UTF8.GetString(payload.ToUtf8Json(1)), StringComparison.Ordinal);
    }

    [Fact]
    public void Unpriced_models_carry_their_tokens_and_no_price()
    {
        var payload = Compose(
            [Sample("2026-10-20T09:00:00Z", model: "mystery-model", uncached: 700, cacheRead: 200, output: 100)],
            "2026-10-28T10:00:00Z");

        var model = Assert.Single(payload.Models);
        Assert.False(model.Priced);
        Assert.Null(model.Price);
        Assert.Equal([700, 200, 0, 0, 100, 0, 0, 0, 1, 1000], Assert.Single(payload.Days).Metrics);
    }

    [Fact]
    public void Europe_London_has_one_grid_and_day_starts_that_follow_the_clock_change()
    {
        var payload = Compose(
            [Sample("2026-10-25T00:30:00Z"), Sample("2026-10-25T01:30:00Z")],
            "2026-10-28T10:00:00Z");

        var starts = payload.HourDayStarts.ToArray();
        Assert.Equal(8, starts.Length);
        Assert.Equal(RemoteUsageComposer.EpochMinutes(Utc("2026-10-21T23:00:00Z")), starts[0]);
        // 25 October has 25 hours: summer time ends.
        var gaps = starts.Zip(starts.Skip(1), (left, right) => right - left).ToArray();
        Assert.Equal([1440, 1440, 1440, 1500, 1440, 1440, 1440], gaps);

        var grid = Assert.Single(payload.Hours);
        Assert.Equal(0, grid.PhaseMinutes);
        Assert.All(grid.Rows, row => Assert.Equal(0, row.Bucket % 60));
        Assert.Equal(2, grid.Rows.Count);
    }

    [Fact]
    public void Asia_Kathmandu_has_one_grid_with_phase_15()
    {
        var payload = Compose(
            [Sample("2026-10-27T03:20:00Z"), Sample("2026-10-27T03:10:00Z")],
            "2026-10-28T10:00:00Z",
            zone: "Asia/Kathmandu");

        var grid = Assert.Single(payload.Hours);
        Assert.Equal(15, grid.PhaseMinutes);
        Assert.All(payload.HourDayStarts, start => Assert.Equal(15, start % 60));
        Assert.Equal(
            [RemoteUsageComposer.EpochMinutes(Utc("2026-10-27T02:15:00Z")), RemoteUsageComposer.EpochMinutes(Utc("2026-10-27T03:15:00Z"))],
            grid.Rows.Select(row => row.Bucket));
    }

    [Fact]
    public void Australia_Lord_Howe_has_two_grids_and_keeps_a_straddling_bucket_per_source_day()
    {
        // Local 2026-10-04 23:45 and 2026-10-05 00:15, both at UTC+11.
        var payload = Compose(
            [Sample("2026-10-04T12:45:00Z", uncached: 100), Sample("2026-10-04T13:15:00Z", uncached: 200)],
            "2026-10-06T02:00:00Z",
            zone: "Australia/Lord_Howe");

        Assert.Equal([0, 30], payload.Hours.Select(grid => grid.PhaseMinutes));
        var dailyFrom = payload.DailyFrom;
        var day4 = new DateOnly(2026, 10, 4).DayNumber - dailyFrom.DayNumber;
        var day5 = day4 + 1;
        var bucket = RemoteUsageComposer.EpochMinutes(Utc("2026-10-04T12:30:00Z"));

        var thirty = payload.Hours.Single(grid => grid.PhaseMinutes == 30);
        Assert.Collection(
            thirty.Rows,
            row =>
            {
                Assert.Equal((day4, bucket), (row.Key, row.Bucket));
                Assert.Equal(100, row.Metrics[0]);
            },
            row =>
            {
                Assert.Equal((day5, bucket), (row.Key, row.Bucket));
                Assert.Equal(200, row.Metrics[0]);
            });

        var zero = payload.Hours.Single(grid => grid.PhaseMinutes == 0);
        Assert.Equal(
            [(day4, bucket - 30), (day5, bucket + 30)],
            zero.Rows.Select(row => (row.Key, row.Bucket)));

        // The range 4 October starts at its +10:30 midnight, phase 30; 5 October at its +11 midnight, phase 0.
        var index = new DateOnly(2026, 10, 4).DayNumber - payload.HourlyFrom!.Value.DayNumber;
        Assert.Equal(30, payload.HourDayStarts[index] % 60);
        Assert.Equal(0, payload.HourDayStarts[index + 1] % 60);
    }

    [Fact]
    public void Rolling_windows_match_the_aggregator_window_reports()
    {
        var zone = Zone("Europe/London");
        var samples = new List<UsageSample>();
        var start = Utc("2026-10-26T08:00:00Z");
        for (var step = 0; step < 60; step++)
        {
            samples.Add(Sample(
                start.AddMinutes(step * 47 + 3).ToString("O", CultureInfo.InvariantCulture),
                model: step % 3 == 0 ? "gpt-test" : "claude-opus-test",
                uncached: 100 + step,
                cacheRead: 1000 * step,
                output: 37 * step,
                requests: 1 + (step % 2)));
        }

        var now = Utc("2026-10-28T10:00:45Z");
        var payload = Compose(samples, "2026-10-28T10:00:45Z");

        foreach (var (rolling, window) in new[]
                 {
                     (payload.Current, UsageTimeWindow.LastHours(24, now)),
                     (payload.Previous, new UsageTimeWindow(UsageTimeWindow.LastHours(24, now).Since.AddHours(-24), UsageTimeWindow.LastHours(24, now).Since))
                 })
        {
            Assert.Equal(window, rolling.Window);
            var report = UsageAggregator.Aggregate(samples, new UsageAggregationOptions
            {
                Window = window,
                Pricing = Pricing,
                TimeZone = zone
            });

            // One account, so every payload row is one aggregator bucket.
            var expected = report.HourlyByModel
                .Select(bucket => (
                    Hour: (bucket.HourStart - window.Since).Ticks / TimeSpan.TicksPerHour,
                    bucket.Model,
                    bucket.Totals.Tokens.UncachedInputTokens,
                    bucket.Totals.Tokens.CacheReadInputTokens,
                    bucket.Totals.Tokens.OutputTokens,
                    Cost: RemoteUsageComposer.ToMicroUsd(bucket.Totals.CostUsd),
                    bucket.Totals.RequestCount))
                .OrderBy(entry => entry.Hour).ThenBy(entry => entry.Model, StringComparer.Ordinal)
                .ToList();
            var actual = rolling.Rows
                .Select(row => (
                    Hour: row.Key,
                    payload.Models[row.Model].Id,
                    row.Metrics[0],
                    row.Metrics[1],
                    row.Metrics[4],
                    Cost: row.Metrics[6],
                    row.Metrics[8]))
                .OrderBy(entry => entry.Hour).ThenBy(entry => entry.Id, StringComparer.Ordinal)
                .ToList();

            Assert.NotEmpty(expected);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Oldest_whole_days_are_dropped_first()
    {
        var samples = Enumerable.Range(0, 10)
            .SelectMany(day => new[]
            {
                Sample($"2026-10-{10 + day:00}T09:00:00Z"),
                Sample($"2026-10-{10 + day:00}T10:00:00Z", model: "gpt-test")
            })
            .ToList();

        var full = Compose(samples, "2026-10-28T10:00:00Z");
        var limited = Compose(samples, "2026-10-28T10:00:00Z", limits: new RemoteUsageLimits { MaxDayRows = 9 });

        // Two rows a day: four whole days fit in nine rows, never a half day.
        Assert.Equal(new DateOnly(2026, 10, 16), limited.DailyFrom);
        Assert.Equal(8, limited.Days.Count);
        Assert.Equal(new DateOnly(2026, 10, 22), limited.HourlyFrom);
        AssertSameDays(full, limited);

        // Renumbering alone saves a few bytes; 200 fewer needs whole days gone.
        var budget = full.ToUtf8Json(RemoteUsagePayload.MaxSafeInteger).Length - 200;
        var bySize = Compose(samples, "2026-10-28T10:00:00Z", limits: new RemoteUsageLimits { MaxBytes = budget });
        Assert.True(bySize.ToUtf8Json(RemoteUsagePayload.MaxSafeInteger).Length <= budget);
        Assert.True(bySize.DailyFrom > new DateOnly(2026, 10, 10));
        Assert.True(bySize.Days.Count < full.Days.Count);
        AssertSameDays(full, bySize);
    }

    private static void AssertSameDays(RemoteUsagePayload full, RemoteUsagePayload cut)
    {
        var offset = cut.DailyFrom.DayNumber - full.DailyFrom.DayNumber;
        Assert.Equal(
            full.Days.Where(row => row.Key >= offset).Select(row => (row.Key - offset, row.Account, row.Model, string.Join(",", row.Metrics))),
            cut.Days.Select(row => (row.Key, row.Account, row.Model, string.Join(",", row.Metrics))));
    }

    [Fact]
    public void Hourly_coverage_shrinks_to_three_days_and_then_is_dropped()
    {
        var samples = Enumerable.Range(0, 7 * 24)
            .Select(hour => Sample(Utc("2026-10-22T00:30:00Z").AddHours(hour).ToString("O", CultureInfo.InvariantCulture)))
            .ToList();

        var reduced = Compose(samples, "2026-10-28T23:00:00Z", limits: new RemoteUsageLimits { MaxHourRows = 100 });
        Assert.Equal(new DateOnly(2026, 10, 26), reduced.HourlyFrom);
        Assert.Equal(new DateOnly(2026, 10, 28), reduced.HourlyTo);
        Assert.Equal(4, reduced.HourDayStarts.Count);
        Assert.True(Assert.Single(reduced.Hours).Rows.Count <= 100);

        var dropped = Compose(samples, "2026-10-28T23:00:00Z", limits: new RemoteUsageLimits { MaxHourRows = 10 });
        Assert.Null(dropped.HourlyFrom);
        Assert.Null(dropped.HourlyTo);
        Assert.Empty(dropped.HourDayStarts);
        Assert.Empty(dropped.Hours);
        var coverage = Json(dropped).GetProperty("coverage");
        Assert.Equal(JsonValueKind.Null, coverage.GetProperty("hourlyFrom").ValueKind);
        Assert.Equal(JsonValueKind.Null, coverage.GetProperty("hourlyTo").ValueKind);
        Assert.Equal(7, dropped.Days.Count);
    }

    [Fact]
    public void Too_many_models_or_accounts_skip_the_upload_with_a_reason()
    {
        var samples = Enumerable.Range(0, 5)
            .Select(index => Sample("2026-10-20T09:00:00Z", model: $"model-{index}"))
            .ToList();
        var source = new RemoteUsageSource(samples, Pricing, DefaultAccounts);

        var models = RemoteUsageComposer.Compose(source, "Europe/London", Zone("Europe/London"), Utc("2026-10-28T10:00:00Z"), new RemoteUsageLimits { MaxModels = 4 });
        Assert.Null(models.Payload);
        Assert.Contains("models", models.SkipReason, StringComparison.Ordinal);

        var accounts = RemoteUsageComposer.Compose(source, "Europe/London", Zone("Europe/London"), Utc("2026-10-28T10:00:00Z"), new RemoteUsageLimits { MaxAccounts = 1 });
        Assert.Null(accounts.Payload);
        Assert.Contains("accounts", accounts.SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_model_ids_are_cut_and_a_collision_merges_into_one_entry()
    {
        var stem = new string('m', 128);
        var payload = Compose(
            [
                Sample("2026-10-20T09:00:00Z", model: stem + "-a", uncached: 10, output: 0),
                Sample("2026-10-20T09:00:00Z", model: stem + "-b", uncached: 5, output: 0)
            ],
            "2026-10-28T10:00:00Z");

        Assert.Equal(stem, Assert.Single(payload.Models).Id);
        Assert.Equal(15, Assert.Single(payload.Days).Metrics[0]);
    }

    [Fact]
    public void Identical_input_gives_identical_bytes_and_the_hash_ignores_the_clock()
    {
        var samples = Enumerable.Range(0, 40)
            .Select(index => Sample(
                Utc("2026-10-01T00:00:00Z").AddHours(index * 13).ToString("O", CultureInfo.InvariantCulture),
                model: index % 2 == 0 ? "gpt-test" : "claude-opus-test",
                account: index % 3 == 0 ? "codex" : "claude-1",
                provider: index % 3 == 0 ? UsageProviderKind.Codex : UsageProviderKind.Claude))
            .ToList();
        var shuffled = samples.OrderBy(sample => sample.Tokens.UncachedInputTokens).ThenByDescending(sample => sample.Timestamp).ToList();

        var first = Compose(samples, "2026-10-28T10:00:00Z");
        var second = Compose(shuffled, "2026-10-28T10:00:00Z");
        Assert.Equal(first.ToUtf8Json(7), second.ToUtf8Json(7));
        Assert.Equal(first.ContentHash, second.ContentHash);

        // A minute later: the clock and rolling bounds moved, the data did not.
        var later = Compose(samples, "2026-10-28T10:01:00Z");
        Assert.Equal(first.ContentHash, later.ContentHash);
        Assert.NotEqual(first.ToUtf8Json(7), later.ToUtf8Json(7));

        var more = Compose([.. samples, Sample("2026-10-27T09:00:00Z")], "2026-10-28T10:00:00Z");
        Assert.NotEqual(first.ContentHash, more.ContentHash);
    }

    [Fact]
    public void Samples_outside_daily_coverage_are_left_out()
    {
        var payload = Compose(
            [Sample("2024-10-27T12:00:00Z"), Sample("2024-10-28T12:00:00Z"), Sample("2026-10-29T12:00:00Z")],
            "2026-10-28T10:00:00Z");

        var row = Assert.Single(payload.Days);
        Assert.Equal(0, row.Key);
        Assert.Equal(new DateOnly(2024, 10, 28), payload.DailyFrom);
    }

    [Fact]
    public void Time_zone_ids_are_converted_to_iana_or_rejected()
    {
        Assert.True(RemoteUsageTimeZone.TryGetIanaId(Zone("Israel Standard Time"), out var converted));
        Assert.Equal("Asia/Jerusalem", converted);

        Assert.True(RemoteUsageTimeZone.TryGetIanaId(Zone("Australia/Lord_Howe"), out var iana));
        Assert.Equal("Australia/Lord_Howe", iana);

        var custom = TimeZoneInfo.CreateCustomTimeZone("Test+3", TimeSpan.FromHours(3), "Test+3", "Test+3");
        Assert.False(RemoteUsageTimeZone.TryGetIanaId(custom, out _));
    }
}
