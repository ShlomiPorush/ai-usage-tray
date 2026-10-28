using System.Text.Json.Nodes;
using costats.Core.Analytics;
using costats.Core.Remote;
using Xunit;

namespace costats.Core.Tests.Remote;

/// <summary>
/// Writes and checks <c>tests/fixtures/remote-usage/crosscheck.json</c>: a
/// fixed sample set composed into one payload per time zone, with the answers
/// the real <see cref="UsageAggregator"/> gives for a set of Usage window
/// views. The viewer's tests recompute every case from the payload alone and
/// must reach the same numbers.
/// </summary>
/// <remarks>
/// Run with <c>UPDATE_USAGE_FIXTURE=1</c> to regenerate the file; otherwise
/// the test fails when the committed file no longer matches.
/// </remarks>
public sealed class RemoteUsageCrossCheckFixtureTests
{
    private const string UpdateVariable = "UPDATE_USAGE_FIXTURE";

    private static readonly ModelPricingTable Pricing = new(
    [
        new KeyValuePair<string, ModelPrice>("claude-opus-fixture", new ModelPrice { InputPerMTok = 15m, CachedInputPerMTok = 1.5m, CacheWrite5mPerMTok = 18.75m, CacheWrite1hPerMTok = 30m, OutputPerMTok = 75m }),
        new KeyValuePair<string, ModelPrice>("claude-haiku-fixture", new ModelPrice { InputPerMTok = 0.8m, CachedInputPerMTok = 0.08m, CacheWrite5mPerMTok = 1m, OutputPerMTok = 4m }),
        new KeyValuePair<string, ModelPrice>("gpt-fixture", new ModelPrice { InputPerMTok = 1.25m, CachedInputPerMTok = 0.125m, OutputPerMTok = 10m })
    ]);

    private const string ArchivedId = "archived:claude:5f0c2a9d7e1b3c46";

    private static readonly RemoteUsageAccountSource[] Accounts =
    [
        new("claude-1", UsageProviderKind.Claude, "Personal"),
        new("claude-2", UsageProviderKind.Claude, "claude-2"),
        new(ArchivedId, UsageProviderKind.Claude, "Old laptop", RemoteUsageAccountKind.Removed),
        new("codex", UsageProviderKind.Codex, UsageAccounts.MergedCodexDisplayName)
    ];

    [Fact]
    public void Cross_check_fixture_matches_the_composer_and_the_aggregator()
    {
        var samples = Samples();
        var fixtures = new JsonArray
        {
            Fixture(samples, "Europe/London", new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero),
                singleDay: new DateOnly(2026, 10, 25), dayAfter: new DateOnly(2026, 10, 26),
                threeDays: (new DateOnly(2026, 10, 24), new DateOnly(2026, 10, 26))),
            Fixture(samples, "Asia/Kathmandu", new DateTimeOffset(2026, 10, 20, 6, 0, 0, TimeSpan.Zero),
                singleDay: new DateOnly(2026, 10, 18), dayAfter: new DateOnly(2026, 10, 19),
                threeDays: (new DateOnly(2026, 10, 17), new DateOnly(2026, 10, 19))),
            Fixture(samples, "Australia/Lord_Howe", new DateTimeOffset(2026, 10, 6, 2, 0, 0, TimeSpan.Zero),
                singleDay: new DateOnly(2026, 10, 4), dayAfter: new DateOnly(2026, 10, 5),
                threeDays: (new DateOnly(2026, 10, 3), new DateOnly(2026, 10, 5)))
        };

        var text = new JsonObject { ["fixtures"] = fixtures }.ToJsonString() + "\n";
        var path = FixturePath();
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return;
        }

        Assert.True(File.Exists(path), $"{path} is missing; run the tests with {UpdateVariable}=1 to create it.");
        var committed = JsonNode.Parse(File.ReadAllText(path))!.ToJsonString() + "\n";
        Assert.True(
            string.Equals(text, committed, StringComparison.Ordinal),
            $"{path} is out of date; run the tests with {UpdateVariable}=1 and review the diff.");
    }

    private static JsonObject Fixture(
        IReadOnlyList<UsageSample> samples,
        string zoneId,
        DateTimeOffset now,
        DateOnly singleDay,
        DateOnly dayAfter,
        (DateOnly From, DateOnly To) threeDays)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var composition = RemoteUsageComposer.Compose(new RemoteUsageSource(samples, Pricing, Accounts), zoneId, zone, now);
        var payload = composition.Payload ?? throw new InvalidOperationException(composition.SkipReason);
        var context = new Context(samples, payload, zone, now);
        var today = UsagePeriods.Today(now, zone);

        var personal = Index(payload, "Personal");
        var codex = Index(payload, UsageAccounts.MergedCodexDisplayName);

        var cases = new JsonArray
        {
            Case(context, "rolling 24 hours", UsagePeriod.Past24Hours, null, null),
            Case(context, "rolling 24 hours vs previous", UsagePeriod.Past24Hours, Comparison.Previous, null),
            Case(context, "single day hourly", UsagePeriod.Day(singleDay), null, null),
            Case(context, "the day after, hourly", UsagePeriod.Day(dayAfter), null, null),
            Case(context, "three days hourly", UsagePeriod.Days(threeDays.From, threeDays.To), Comparison.Previous, null),
            Case(context, "7 days daily", UsagePeriod.LastDays(7, today), null, null),
            Case(context, "30 days vs previous", UsagePeriod.LastDays(30, today), Comparison.Previous, null),
            Case(context, "7 days vs custom", UsagePeriod.LastDays(7, today), Comparison.Days(today.AddDays(-20), today.AddDays(-14)), null),
            Case(context, "7 days, two accounts", UsagePeriod.LastDays(7, today), null, [personal, codex]),
            Case(context, "single day hourly, one account", UsagePeriod.Day(singleDay), null, [personal])
        };

        return new JsonObject
        {
            ["zone"] = zoneId,
            ["payload"] = JsonNode.Parse(payload.ToUtf8Json(now.ToUnixTimeMilliseconds())),
            ["cases"] = cases
        };
    }

    private static int Index(RemoteUsagePayload payload, string name) =>
        payload.Accounts.Select((account, index) => (account, index)).Single(entry => entry.account.Name == name).index;

    private static JsonObject Case(Context context, string name, UsagePeriod period, Comparison? comparison, int[]? accounts)
    {
        var accountIds = accounts?.Select(index => AccountIdOf(context.Payload, index)).ToArray();
        var resolved = UsagePeriods.Resolve(period, context.Now, context.Zone);
        UsageResolvedPeriod? compared = comparison is null
            ? null
            : UsagePeriods.ResolveComparison(period, comparison.Picked, context.Now, context.Zone);

        var expected = Expected(context, resolved, accountIds);
        expected["comparison"] = compared is null ? null : Expected(context, compared, accountIds);

        return new JsonObject
        {
            ["name"] = name,
            ["period"] = period.IsRolling
                ? new JsonObject { ["kind"] = "rolling" }
                : new JsonObject { ["kind"] = "days", ["from"] = RemoteUsageComposer.FormatDay(period.From), ["to"] = RemoteUsageComposer.FormatDay(period.To) },
            ["comparison"] = comparison switch
            {
                null => null,
                { Picked: null } => "previous",
                { Picked: { } days } => new JsonObject { ["from"] = RemoteUsageComposer.FormatDay(days.From), ["to"] = RemoteUsageComposer.FormatDay(days.To) }
            },
            ["accounts"] = accounts is null ? null : new JsonArray([.. accounts.Select(index => (JsonNode)index)]),
            ["expected"] = expected
        };
    }

    /// <summary>The internal id behind a payload account, for the aggregator's filter only.</summary>
    private static string AccountIdOf(RemoteUsagePayload payload, int index) => payload.Accounts[index].Name switch
    {
        "Personal" => "claude-1",
        "Old laptop (removed)" => ArchivedId,
        UsageAccounts.MergedCodexDisplayName => UsageAccounts.MergedCodexId,
        _ => "claude-2"
    };

    /// <summary>What the desktop Usage window shows for one resolved period.</summary>
    private static JsonObject Expected(Context context, UsageResolvedPeriod resolved, IReadOnlyCollection<string>? accountIds)
    {
        // The desktop asks for a window report when the period is hourly and
        // for a day range otherwise, exactly as here.
        var report = UsageAggregator.Aggregate(context.Samples, new UsageAggregationOptions
        {
            Range = resolved.Window is null ? resolved.Range : UsageDateRange.All,
            Window = resolved.Window,
            AccountIds = accountIds,
            Pricing = Pricing,
            TimeZone = context.Zone,
            GeneratedAt = context.Now
        });

        var points = new JsonArray();
        if (resolved.Window is { } window)
        {
            var hourly = report.Hourly.ToDictionary(hour => hour.HourStart, hour => hour.Totals);
            foreach (var start in window.HourStarts())
            {
                points.Add(new JsonObject
                {
                    ["start"] = RemoteUsageComposer.FormatInstant(start),
                    ["totals"] = Totals(hourly.GetValueOrDefault(start, UsageTotals.Empty))
                });
            }
        }
        else
        {
            var daily = report.Daily.ToDictionary(day => day.Day, day => day.Totals);
            for (var day = resolved.FirstDay; day <= resolved.LastDay; day = day.AddDays(1))
            {
                points.Add(new JsonObject
                {
                    ["start"] = RemoteUsageComposer.FormatDay(day),
                    ["totals"] = Totals(daily.GetValueOrDefault(day, UsageTotals.Empty))
                });
            }
        }

        var models = context.Payload.Models;
        var byModel = report.ByModel
            .Select(model => (
                Index: models.Select((entry, index) => (entry, index))
                    .Single(entry => entry.entry.Id == model.Model && entry.entry.Provider == (model.Provider == UsageProviderKind.Codex ? "codex" : "claude"))
                    .index,
                model.Totals))
            .OrderBy(model => model.Index);

        return new JsonObject
        {
            ["totals"] = Totals(report.Totals),
            ["points"] = points,
            ["byModel"] = new JsonArray([.. byModel.Select(model => (JsonNode)new JsonObject
            {
                ["modelIdx"] = model.Index,
                ["totals"] = Totals(model.Totals)
            })])
        };
    }

    private static JsonObject Totals(UsageTotals totals) => new()
    {
        ["uncached"] = totals.Tokens.UncachedInputTokens,
        ["cacheRead"] = totals.Tokens.CacheReadInputTokens,
        ["cacheWrite5m"] = totals.Tokens.CacheWrite5mInputTokens,
        ["cacheWrite1h"] = totals.Tokens.CacheWrite1hInputTokens,
        ["output"] = totals.Tokens.OutputTokens,
        ["reasoning"] = totals.Tokens.ReasoningOutputTokens,
        ["costMicroUsd"] = RemoteUsageComposer.ToMicroUsd(totals.CostUsd),
        ["savingsMicroUsd"] = RemoteUsageComposer.ToMicroUsd(totals.CacheSavingsUsd),
        ["requests"] = totals.RequestCount,
        ["unpricedTokens"] = totals.UnpricedTokens
    };

    /// <summary>
    /// A fixed, generated history from mid July to the end of October 2026,
    /// plus the Lord Howe samples either side of its 4 October midnight.
    /// </summary>
    private static List<UsageSample> Samples()
    {
        var random = new Lcg(20261028);
        var samples = new List<UsageSample>();
        var removedAt = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        for (var at = new DateTimeOffset(2026, 7, 15, 0, 7, 0, TimeSpan.Zero);
             at < new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
             at = at.AddMinutes(40 + random.Next(170)))
        {
            var pick = random.Next(100);
            var (account, provider) = pick switch
            {
                < 45 => ("claude-1", UsageProviderKind.Claude),
                < 65 => ("claude-2", UsageProviderKind.Claude),
                < 75 when at < removedAt => (ArchivedId, UsageProviderKind.Claude),
                < 75 => ("claude-1", UsageProviderKind.Claude),
                _ => ("codex", UsageProviderKind.Codex)
            };

            var model = provider == UsageProviderKind.Claude
                ? (random.Next(10) < 4 ? "claude-opus-fixture" : "claude-haiku-fixture")
                : (random.Next(10) < 8 ? "gpt-fixture" : "mystery-unpriced");
            var output = random.Next(8000);
            samples.Add(new UsageSample(at, provider, account, model, new UsageTokens
            {
                UncachedInputTokens = random.Next(5000),
                CacheReadInputTokens = random.Next(200_000),
                CacheWrite5mInputTokens = provider == UsageProviderKind.Claude ? random.Next(20_000) : 0,
                CacheWrite1hInputTokens = provider == UsageProviderKind.Claude && random.Next(4) == 0 ? random.Next(5000) : 0,
                OutputTokens = output,
                ReasoningOutputTokens = output / (2 + random.Next(4))
            })
            {
                // Saved history buckets stand for several requests.
                Requests = account == ArchivedId ? 1 + random.Next(4) : 1
            });
        }

        // Local 2026-10-04 23:45 and 2026-10-05 00:15 on Lord Howe Island (UTC+11).
        foreach (var at in new[] { new DateTimeOffset(2026, 10, 4, 12, 45, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 4, 13, 15, 0, TimeSpan.Zero) })
        {
            samples.Add(new UsageSample(at, UsageProviderKind.Claude, "claude-1", "claude-opus-fixture", new UsageTokens
            {
                UncachedInputTokens = 1234,
                CacheReadInputTokens = 56_789,
                OutputTokens = 4321,
                ReasoningOutputTokens = 1000
            }));
        }

        return samples;
    }

    private static string FixturePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "costats.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("Repository root not found."),
            "tests", "fixtures", "remote-usage", "crosscheck.json");
    }

    private sealed record Context(IReadOnlyList<UsageSample> Samples, RemoteUsagePayload Payload, TimeZoneInfo Zone, DateTimeOffset Now);

    /// <summary>The comparison of a case: the previous period, or picked days.</summary>
    private sealed record Comparison(UsagePeriod? Picked)
    {
        public static Comparison Previous { get; } = new((UsagePeriod?)null);

        public static Comparison Days(DateOnly from, DateOnly to) => new(UsagePeriod.Days(from, to));
    }

    /// <summary>A tiny fixed generator, so the fixture never depends on <see cref="Random"/>'s algorithm.</summary>
    private sealed class Lcg(uint seed)
    {
        private uint _state = seed;

        public int Next(int maxExclusive)
        {
            _state = unchecked((_state * 1_664_525u) + 1_013_904_223u);
            return (int)((_state >> 8) % (uint)maxExclusive);
        }
    }
}
