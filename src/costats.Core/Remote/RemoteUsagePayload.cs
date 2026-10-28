using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using costats.Core.Analytics;

namespace costats.Core.Remote;

/// <summary>What kind of account a <see cref="RemoteUsageAccountSource"/> is.</summary>
public enum RemoteUsageAccountKind
{
    /// <summary>An account configured in Settings.</summary>
    Configured,

    /// <summary>Saved history of an account that is no longer configured.</summary>
    Removed,

    /// <summary>Recovered Claude history whose account can no longer be told.</summary>
    UnknownHistory
}

/// <summary>
/// One account the usage samples may reference. The internal
/// <see cref="AccountId"/> only matches samples to accounts; it never reaches
/// the payload.
/// </summary>
/// <param name="AccountId">The id the samples carry.</param>
/// <param name="Provider">Which agent the account belongs to.</param>
/// <param name="Nickname">
/// The user's own name for the account, or null when it has none. A value
/// equal to <paramref name="AccountId"/> counts as none.
/// </param>
/// <param name="Kind">Configured, removed or unknown history.</param>
public sealed record RemoteUsageAccountSource(
    string AccountId,
    UsageProviderKind Provider,
    string? Nickname,
    RemoteUsageAccountKind Kind = RemoteUsageAccountKind.Configured);

/// <summary>
/// Everything the usage composer needs: the same samples, prices and accounts
/// the desktop Usage window reports from.
/// </summary>
/// <param name="Samples">Live and saved usage combined, after the deletion floor.</param>
/// <param name="Pricing">The price table the desktop costs with.</param>
/// <param name="Accounts">Accounts the samples may reference.</param>
public sealed record RemoteUsageSource(
    IReadOnlyList<UsageSample> Samples,
    ModelPricingTable Pricing,
    IReadOnlyList<RemoteUsageAccountSource> Accounts);

/// <summary>
/// The size the composer keeps a payload within. The defaults are the
/// composer column of the shared contract; the relay accepts a little more.
/// </summary>
public sealed record RemoteUsageLimits
{
    public static RemoteUsageLimits Default { get; } = new();

    public int MaxBytes { get; init; } = 900_000;
    public int MaxAccounts { get; init; } = 48;
    public int MaxModels { get; init; } = 384;
    public int MaxDayRows { get; init; } = 50_000;
    public int MaxDailyDays { get; init; } = 731;
    public int HourlyDays { get; init; } = 7;
    public int ReducedHourlyDays { get; init; } = 3;
    public int MaxHourRows { get; init; } = 30_000;
    public int MaxRollingRows { get; init; } = 4_000;
    public int MaxNameLength { get; init; } = 64;
    public int MaxModelIdLength { get; init; } = 128;
}

/// <summary>One account as published: a display name and a provider, nothing else.</summary>
public sealed record RemoteUsageAccount(string Name, string Provider);

/// <summary>
/// One model as published. <see cref="Price"/> is null for an unpriced model.
/// </summary>
public sealed record RemoteUsageModel(string Id, string Provider, bool Priced, ModelPrice? Price);

/// <summary>
/// One row of the payload: the leading key columns and the ten metrics.
/// <see cref="Key"/> is the day number for days and hours rows and the hour
/// index for rolling rows; <see cref="Bucket"/> is the bucket start in UTC
/// epoch minutes for hours rows and unused otherwise.
/// </summary>
public sealed record RemoteUsageRow(long Key, long Bucket, int Account, int Model, IReadOnlyList<long> Metrics);

/// <summary>One hourly grid: every hourly-coverage sample on buckets of one phase.</summary>
public sealed record RemoteUsageHourGrid(int PhaseMinutes, IReadOnlyList<RemoteUsageRow> Rows);

/// <summary>One rolling 24-hour window.</summary>
public sealed record RemoteUsageRollingWindow(UsageTimeWindow Window, IReadOnlyList<RemoteUsageRow> Rows);

/// <summary>
/// A composed usage payload, version 1. Immutable; <see cref="ToUtf8Json"/>
/// writes it with the write sequence it is sent under.
/// </summary>
public sealed class RemoteUsagePayload
{
    /// <summary>Payload schema version.</summary>
    public const int Version = 1;

    /// <summary>Largest JSON safe integer, used to measure a payload before its sequence is known.</summary>
    public const long MaxSafeInteger = 9_007_199_254_740_991;

    private readonly IReadOnlyList<RemoteUsageRow> _allDays;
    private readonly IReadOnlyList<RemoteUsageHourGrid> _hours;
    private readonly int _dayCut;
    private readonly int _firstKeptDay;
    private string? _contentHash;

    internal RemoteUsagePayload(
        DateTimeOffset generatedAt,
        string timeZone,
        DateOnly baseDay,
        DateOnly dailyTo,
        int dayCut,
        DateOnly? hourlyFrom,
        DateOnly? hourlyTo,
        IReadOnlyList<RemoteUsageAccount> accounts,
        IReadOnlyList<RemoteUsageModel> models,
        IReadOnlyList<RemoteUsageRow> allDays,
        IReadOnlyList<long> hourDayStarts,
        IReadOnlyList<RemoteUsageHourGrid> hours,
        RemoteUsageRollingWindow current,
        RemoteUsageRollingWindow previous)
    {
        GeneratedAt = generatedAt;
        TimeZone = timeZone;
        DailyFrom = baseDay.AddDays(dayCut);
        DailyTo = dailyTo;
        HourlyFrom = hourlyFrom;
        HourlyTo = hourlyTo;
        Accounts = accounts;
        Models = models;
        HourDayStarts = hourDayStarts;
        _hours = hours;
        Current = current;
        Previous = previous;
        _allDays = allDays;
        _dayCut = dayCut;
        _firstKeptDay = FirstRowAtOrAfter(allDays, dayCut);
    }

    /// <summary>The clock reading the payload describes, whole seconds, UTC.</summary>
    public DateTimeOffset GeneratedAt { get; }

    /// <summary>IANA id of the zone whose days the payload uses.</summary>
    public string TimeZone { get; }

    public DateOnly DailyFrom { get; }

    public DateOnly DailyTo { get; }

    public DateOnly? HourlyFrom { get; }

    public DateOnly? HourlyTo { get; }

    public IReadOnlyList<RemoteUsageAccount> Accounts { get; }

    public IReadOnlyList<RemoteUsageModel> Models { get; }

    /// <summary>Days rows, numbered from <see cref="DailyFrom"/>.</summary>
    public IReadOnlyList<RemoteUsageRow> Days =>
        [.. _allDays.Skip(_firstKeptDay).Select(row => row with { Key = row.Key - _dayCut })];

    /// <summary>Number of <see cref="Days"/> rows, without building them.</summary>
    public int DayRowCount => _allDays.Count - _firstKeptDay;

    public IReadOnlyList<long> HourDayStarts { get; }

    /// <summary>Hourly grids; their rows are numbered from <see cref="DailyFrom"/>.</summary>
    public IReadOnlyList<RemoteUsageHourGrid> Hours =>
        [.. _hours.Select(grid => grid with { Rows = [.. grid.Rows.Select(row => row with { Key = row.Key - _dayCut })] })];

    public RemoteUsageRollingWindow Current { get; }

    public RemoteUsageRollingWindow Previous { get; }

    /// <summary>
    /// A hash of everything that is not expected to move on its own: the
    /// payload without <c>seq</c>, <c>generatedAt</c> and the four rolling
    /// window bounds. Two compositions with the same hash carry the same data.
    /// </summary>
    public string ContentHash =>
        _contentHash ??= Convert.ToHexStringLower(SHA256.HashData(Write(0, forHash: true)));

    /// <summary>The payload as UTF-8 JSON, sent under <paramref name="seq"/>.</summary>
    public byte[] ToUtf8Json(long seq)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(seq, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(seq, MaxSafeInteger);
        return Write(seq, forHash: false);
    }

    internal RemoteUsagePayload WithDayCut(int dayCut) => new(
        GeneratedAt,
        TimeZone,
        DailyFrom.AddDays(-_dayCut),
        DailyTo,
        dayCut,
        HourlyFrom,
        HourlyTo,
        Accounts,
        Models,
        _allDays,
        HourDayStarts,
        _hours,
        Current,
        Previous);

    private static int FirstRowAtOrAfter(IReadOnlyList<RemoteUsageRow> rows, long day)
    {
        int low = 0, high = rows.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (rows[middle].Key < day)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private byte[] Write(long seq, bool forHash)
    {
        var buffer = new ArrayBufferWriter<byte>(64 * 1024);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            if (!forHash)
            {
                writer.WriteNumber("seq", seq);
                writer.WriteString("generatedAt", RemoteUsageComposer.FormatInstant(GeneratedAt));
            }

            writer.WriteString("timeZone", TimeZone);

            writer.WriteStartObject("coverage");
            writer.WriteString("dailyFrom", RemoteUsageComposer.FormatDay(DailyFrom));
            writer.WriteString("dailyTo", RemoteUsageComposer.FormatDay(DailyTo));
            WriteDayOrNull(writer, "hourlyFrom", HourlyFrom);
            WriteDayOrNull(writer, "hourlyTo", HourlyTo);
            writer.WriteEndObject();

            writer.WriteStartArray("accounts");
            foreach (var account in Accounts)
            {
                writer.WriteStartObject();
                writer.WriteString("name", account.Name);
                writer.WriteString("provider", account.Provider);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("models");
            foreach (var model in Models)
            {
                writer.WriteStartObject();
                writer.WriteString("id", model.Id);
                writer.WriteString("provider", model.Provider);
                writer.WriteBoolean("priced", model.Priced);
                if (model.Price is { } price)
                {
                    writer.WriteStartObject("price");
                    WriteRate(writer, "input", price.InputPerMTok);
                    WriteRate(writer, "cacheRead", price.CachedInputPerMTok);
                    WriteRate(writer, "cacheWrite5m", price.CacheWrite5mPerMTok);
                    WriteRate(writer, "cacheWrite1h", price.CacheWrite1hPerMTok);
                    WriteRate(writer, "output", price.OutputPerMTok);
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteNull("price");
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("days");
            for (var index = _firstKeptDay; index < _allDays.Count; index++)
            {
                WriteRow(writer, _allDays[index], _allDays[index].Key - _dayCut, bucket: null);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("hourDayStarts");
            foreach (var start in HourDayStarts)
            {
                writer.WriteNumberValue(start);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("hours");
            foreach (var grid in _hours)
            {
                writer.WriteStartObject();
                writer.WriteNumber("phaseMinutes", grid.PhaseMinutes);
                writer.WriteStartArray("rows");
                foreach (var row in grid.Rows)
                {
                    WriteRow(writer, row, row.Key - _dayCut, row.Bucket);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartObject("rolling");
            WriteRolling(writer, "current", Current, forHash);
            WriteRolling(writer, "previous", Previous, forHash);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteRolling(Utf8JsonWriter writer, string name, RemoteUsageRollingWindow rolling, bool forHash)
    {
        writer.WriteStartObject(name);
        if (!forHash)
        {
            writer.WriteString("since", RemoteUsageComposer.FormatInstant(rolling.Window.Since));
            writer.WriteString("until", RemoteUsageComposer.FormatInstant(rolling.Window.Until));
        }

        writer.WriteStartArray("rows");
        foreach (var row in rolling.Rows)
        {
            WriteRow(writer, row, row.Key, bucket: null);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteRow(Utf8JsonWriter writer, RemoteUsageRow row, long key, long? bucket)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(key);
        if (bucket is { } start)
        {
            writer.WriteNumberValue(start);
        }

        writer.WriteNumberValue(row.Account);
        writer.WriteNumberValue(row.Model);
        foreach (var metric in row.Metrics)
        {
            writer.WriteNumberValue(metric);
        }

        writer.WriteEndArray();
    }

    private static void WriteDayOrNull(Utf8JsonWriter writer, string name, DateOnly? day)
    {
        if (day is { } value)
        {
            writer.WriteString(name, RemoteUsageComposer.FormatDay(value));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteRate(Utf8JsonWriter writer, string name, decimal? rate)
    {
        if (rate is { } value)
        {
            // Dividing by a one with many zeros drops trailing zeros, so 15.00
            // and 15 write the same and the hash does not depend on the scale.
            writer.WriteNumber(name, value / 1.000000000000000000000000000000000m);
        }
        else
        {
            writer.WriteNull(name);
        }
    }
}

/// <summary>Result of one composition: a payload, or why there is none.</summary>
public sealed record RemoteUsageComposition(RemoteUsagePayload? Payload, string? SkipReason)
{
    public bool IsSkipped => Payload is null;
}

/// <summary>
/// Builds the remote usage payload (version 1) from the samples the desktop
/// Usage window reports from. Pure: no IO and no clock beyond the
/// <c>generatedAt</c> passed in, deterministic for a given input.
/// </summary>
/// <remarks>
/// Every bucket is costed once from its summed tokens through
/// <see cref="UsageCostCalculator"/>, with the price lookup
/// <see cref="UsageAggregator"/> uses, and rounded to micro-dollars half to
/// even. Local days follow the aggregator's rule (the day of the sample's UTC
/// instant in the zone), and the hourly grids reproduce the desktop's hourly
/// day ranges exactly, including zones whose midnights are not on the hour.
/// </remarks>
public static class RemoteUsageComposer
{
    /// <summary>Name of the single Codex account.</summary>
    public const string CodexAccountName = UsageAccounts.MergedCodexDisplayName;

    /// <summary>Name of recovered Claude history whose account is unknown.</summary>
    public const string UnknownClaudeAccountName = UsageHistory.UnknownClaudeAccountName;

    /// <summary>Name prefix of a Claude account without a nickname.</summary>
    public const string UnnamedClaudeAccountPrefix = "Claude account";

    /// <summary>Suffix of an account that is no longer configured.</summary>
    public const string RemovedSuffix = " (removed)";

    /// <summary>Number of metric columns in every row.</summary>
    public const int MetricCount = 10;

    private const long MaxMetric = 1_000_000_000_000_000;

    /// <summary>Composes the payload, or says why it cannot be sent.</summary>
    /// <param name="source">Samples, prices and accounts.</param>
    /// <param name="timeZoneId">The IANA id of <paramref name="zone"/>.</param>
    /// <param name="zone">The zone the desktop buckets days by.</param>
    /// <param name="generatedAt">The clock reading; cut to whole seconds.</param>
    /// <param name="limits">Size limits, <see cref="RemoteUsageLimits.Default"/> when null.</param>
    public static RemoteUsageComposition Compose(
        RemoteUsageSource source,
        string timeZoneId,
        TimeZoneInfo zone,
        DateTimeOffset generatedAt,
        RemoteUsageLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        ArgumentNullException.ThrowIfNull(zone);
        limits ??= RemoteUsageLimits.Default;
        var pricing = source.Pricing ?? ModelPricingTable.Default;

        var now = new DateTimeOffset(generatedAt.UtcTicks - (generatedAt.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
        var today = UsagePeriods.Today(now, zone);
        var baseDay = today.AddDays(-(limits.MaxDailyDays - 1));
        var current = UsageTimeWindow.LastHours(24, now);
        var previous = new UsageTimeWindow(current.Since.AddHours(-24), current.Since);

        // Only samples some section can show matter: inside daily coverage,
        // or inside one of the rolling windows.
        var relevant = new List<LocatedSample>();
        foreach (var sample in source.Samples ?? [])
        {
            if (sample is null)
            {
                continue;
            }

            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(sample.Timestamp, zone).DateTime);
            var inDays = day >= baseDay && day <= today;
            if (inDays || current.Contains(sample.Timestamp) || previous.Contains(sample.Timestamp))
            {
                relevant.Add(new LocatedSample(sample, day.DayNumber - baseDay.DayNumber, inDays));
            }
        }

        var accounts = AccountTable.Build(source.Accounts ?? [], relevant, limits.MaxNameLength);
        if (accounts.Published.Count > limits.MaxAccounts)
        {
            return Skip($"{accounts.Published.Count} accounts is over the limit of {limits.MaxAccounts}");
        }

        var models = ModelTable.Build(relevant, pricing, limits.MaxModelIdLength);
        if (models.Published.Count > limits.MaxModels)
        {
            return Skip($"{models.Published.Count} models is over the limit of {limits.MaxModels}");
        }

        var days = BuildRows(
            relevant.Where(located => located.InDays),
            located => (located.Day, 0),
            accounts,
            models,
            static (left, right) => Compare(left.Key, right.Key, left.Account, right.Account, left.Model, right.Model));

        var currentRows = BuildRolling(relevant, current, accounts, models);
        var previousRows = BuildRolling(relevant, previous, accounts, models);
        if (currentRows.Count > limits.MaxRollingRows || previousRows.Count > limits.MaxRollingRows)
        {
            return Skip($"the rolling 24 hours need more than {limits.MaxRollingRows} rows");
        }

        if (FirstInvalidMetric(days.Concat(currentRows).Concat(previousRows)) is { } invalid)
        {
            return Skip(invalid);
        }

        // Drop policy: whole oldest days go first, never part of a day. Only
        // when the hourly section itself is in the way is it shrunk to three
        // days, and then dropped.
        foreach (var hourlyDays in new[] { limits.HourlyDays, limits.ReducedHourlyDays, 0 }.Distinct())
        {
            var hourly = BuildHourly(relevant, zone, today, baseDay, hourlyDays, accounts, models);
            if (hourly.Rows > limits.MaxHourRows)
            {
                continue;
            }

            if (FirstInvalidMetric(hourly.Grids.SelectMany(grid => grid.Rows)) is { } invalidHour)
            {
                return Skip(invalidHour);
            }

            var candidate = new RemoteUsagePayload(
                now,
                timeZoneId,
                baseDay,
                today,
                0,
                hourly.From,
                hourly.From is null ? null : today,
                accounts.Published,
                models.Published,
                days,
                hourly.DayStarts,
                hourly.Grids,
                new RemoteUsageRollingWindow(current, currentRows),
                new RemoteUsageRollingWindow(previous, previousRows));

            // Hourly coverage must stay inside daily coverage.
            var maxCut = hourly.From is { } hourlyFrom
                ? hourlyFrom.DayNumber - baseDay.DayNumber
                : today.DayNumber - baseDay.DayNumber;

            if (!Fits(candidate.WithDayCut(maxCut), limits))
            {
                continue;
            }

            // Size only falls as the cut moves forward, so the smallest cut
            // that fits is found by bisection.
            int low = 0, high = maxCut;
            while (low < high)
            {
                var middle = low + ((high - low) / 2);
                if (Fits(candidate.WithDayCut(middle), limits))
                {
                    high = middle;
                }
                else
                {
                    low = middle + 1;
                }
            }

            return new RemoteUsageComposition(low == 0 ? candidate : candidate.WithDayCut(low), null);
        }

        return Skip($"the payload stays over {limits.MaxBytes} bytes even with only today and no hourly data");
    }

    /// <summary>
    /// True when a saved account name is really an id the app generated
    /// (<c>claude-2</c>, <c>codex</c>): an account without a nickname is
    /// saved under its id, which must not be published as a name.
    /// </summary>
    public static bool LooksLikeAccountId(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        foreach (var prefix in new[] { "claude", "codex" })
        {
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = trimmed[prefix.Length..];
            if (rest.Length == 0 || (rest.Length > 1 && rest[0] == '-' && rest[1..].All(char.IsAsciiDigit)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A day as the payload writes it: <c>YYYY-MM-DD</c>.</summary>
    public static string FormatDay(DateOnly day) =>
        day.ToString("yyyy'-'MM'-'dd", CultureInfo.InvariantCulture);

    /// <summary>An instant as the payload writes it: UTC, whole seconds, with Z.</summary>
    public static string FormatInstant(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>US dollars as micro-dollars, rounded half to even.</summary>
    public static long ToMicroUsd(decimal usd) =>
        (long)Math.Round(usd * 1_000_000m, MidpointRounding.ToEven);

    /// <summary>UTC epoch minutes of an instant that falls on a whole minute.</summary>
    public static long EpochMinutes(DateTimeOffset instant) =>
        (instant.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / TimeSpan.TicksPerMinute;

    /// <summary>
    /// The ten metrics of a bucket: tokens by type, cost and savings in
    /// micro-dollars, requests, and the tokens of an unpriced model.
    /// </summary>
    public static long[] Metrics(UsageTokens tokens, long requests, ModelPrice price)
    {
        var cost = UsageCostCalculator.Compute(tokens, price);
        return
        [
            tokens.UncachedInputTokens,
            tokens.CacheReadInputTokens,
            tokens.CacheWrite5mInputTokens,
            tokens.CacheWrite1hInputTokens,
            tokens.OutputTokens,
            tokens.ReasoningOutputTokens,
            ToMicroUsd(cost.CostUsd),
            ToMicroUsd(cost.CacheSavingsUsd),
            requests,
            cost.IsPriced ? 0 : tokens.ProcessedTokens
        ];
    }

    private static RemoteUsageComposition Skip(string reason) => new(null, reason);

    private static bool Fits(RemoteUsagePayload payload, RemoteUsageLimits limits) =>
        payload.DayRowCount <= limits.MaxDayRows &&
        payload.ToUtf8Json(RemoteUsagePayload.MaxSafeInteger).Length <= limits.MaxBytes;

    private static string? FirstInvalidMetric(IEnumerable<RemoteUsageRow> rows)
    {
        foreach (var row in rows)
        {
            for (var column = 0; column < row.Metrics.Count; column++)
            {
                var value = row.Metrics[column];
                var allowsNegative = column == 7;
                if (Math.Abs(value) > MaxMetric || (!allowsNegative && value < 0))
                {
                    return "a usage value is outside the range the relay accepts";
                }
            }
        }

        return null;
    }

    private static List<RemoteUsageRow> BuildRolling(
        List<LocatedSample> relevant,
        UsageTimeWindow window,
        AccountTable accounts,
        ModelTable models) =>
        BuildRows(
            relevant.Where(located => window.Contains(located.Sample.Timestamp)),
            located => ((window.HourStartOf(located.Sample.Timestamp) - window.Since).Ticks / TimeSpan.TicksPerHour, 0),
            accounts,
            models,
            static (left, right) => Compare(left.Key, right.Key, left.Account, right.Account, left.Model, right.Model));

    private static HourlySection BuildHourly(
        List<LocatedSample> relevant,
        TimeZoneInfo zone,
        DateOnly today,
        DateOnly baseDay,
        int hourlyDays,
        AccountTable accounts,
        ModelTable models)
    {
        if (hourlyDays <= 0)
        {
            return new HourlySection(null, [], [], 0);
        }

        var from = today.AddDays(-(hourlyDays - 1));
        var dayStarts = new long[hourlyDays + 1];
        for (var index = 0; index <= hourlyDays; index++)
        {
            dayStarts[index] = EpochMinutes(UsagePeriods.StartOfDay(from.AddDays(index), zone));
        }

        var firstDay = from.DayNumber - baseDay.DayNumber;
        var lastDay = today.DayNumber - baseDay.DayNumber;
        var inCoverage = relevant
            .Where(located => located.InDays && located.Day >= firstDay && located.Day <= lastDay)
            .ToList();

        var grids = new List<RemoteUsageHourGrid>();
        var rowCount = 0;
        foreach (var phase in dayStarts.Take(hourlyDays).Select(start => (int)FloorMod(start, 60)).Distinct().Order())
        {
            var phaseTicks = phase * TimeSpan.TicksPerMinute;
            var rows = BuildRows(
                inCoverage,
                located =>
                {
                    var ticks = located.Sample.Timestamp.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
                    var bucket = (FloorDiv(ticks - phaseTicks, TimeSpan.TicksPerHour) * TimeSpan.TicksPerHour) + phaseTicks;
                    return (located.Day, bucket / TimeSpan.TicksPerMinute);
                },
                accounts,
                models,
                static (left, right) =>
                {
                    var byBucket = left.Bucket.CompareTo(right.Bucket);
                    return byBucket != 0
                        ? byBucket
                        : Compare(left.Key, right.Key, left.Account, right.Account, left.Model, right.Model);
                });

            rowCount += rows.Count;
            grids.Add(new RemoteUsageHourGrid(phase, rows));
        }

        return new HourlySection(from, dayStarts, grids, rowCount);
    }

    /// <summary>
    /// Folds samples into rows. Tokens are summed per exact model id first and
    /// costed once per bucket; models whose ids collide after the length cut
    /// are then added together into one row.
    /// </summary>
    private static List<RemoteUsageRow> BuildRows(
        IEnumerable<LocatedSample> samples,
        Func<LocatedSample, (long Key, long Bucket)> keyOf,
        AccountTable accounts,
        ModelTable models,
        Comparison<RemoteUsageRow> order)
    {
        var buckets = new Dictionary<(long Key, long Bucket, int Account, int FullModel), (UsageTokens Tokens, long Requests)>();
        foreach (var located in samples)
        {
            var (key, bucket) = keyOf(located);
            var account = accounts.IndexOf(located.Sample);
            var fullModel = models.FullIndexOf(located.Sample);
            var id = (key, bucket, account, fullModel);
            buckets[id] = buckets.TryGetValue(id, out var existing)
                ? (existing.Tokens + located.Sample.Tokens, existing.Requests + located.Sample.Requests)
                : (located.Sample.Tokens, located.Sample.Requests);
        }

        var merged = new Dictionary<(long Key, long Bucket, int Account, int Model), long[]>();
        foreach (var (id, totals) in buckets)
        {
            var full = models.Full[id.FullModel];
            var metrics = Metrics(totals.Tokens, totals.Requests, full.Price);
            var rowId = (id.Key, id.Bucket, id.Account, full.Index);
            if (merged.TryGetValue(rowId, out var existing))
            {
                for (var column = 0; column < MetricCount; column++)
                {
                    existing[column] += metrics[column];
                }
            }
            else
            {
                merged[rowId] = metrics;
            }
        }

        var rows = merged
            .Select(entry => new RemoteUsageRow(entry.Key.Key, entry.Key.Bucket, entry.Key.Account, entry.Key.Model, entry.Value))
            .ToList();
        rows.Sort(order);
        return rows;
    }

    private static int Compare(long leftKey, long rightKey, int leftAccount, int rightAccount, int leftModel, int rightModel)
    {
        var byKey = leftKey.CompareTo(rightKey);
        if (byKey != 0)
        {
            return byKey;
        }

        var byAccount = leftAccount.CompareTo(rightAccount);
        return byAccount != 0 ? byAccount : leftModel.CompareTo(rightModel);
    }

    private static long FloorDiv(long value, long divisor)
    {
        var quotient = value / divisor;
        return (value % divisor != 0 && (value < 0) != (divisor < 0)) ? quotient - 1 : quotient;
    }

    private static long FloorMod(long value, long divisor) => value - (FloorDiv(value, divisor) * divisor);

    internal static string ProviderName(UsageProviderKind provider) =>
        provider == UsageProviderKind.Codex ? "codex" : "claude";

    /// <summary>Trims a name and cuts it to <paramref name="max"/> UTF-16 units.</summary>
    internal static string Cut(string value, int max) => CutId(value.Trim(), max);

    /// <summary>
    /// Cuts to <paramref name="max"/> UTF-16 units without splitting a
    /// surrogate pair. Model ids are not trimmed: they are ids, not names.
    /// </summary>
    internal static string CutId(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }

        var cut = value[..Math.Max(0, max)];
        return cut.Length > 0 && char.IsHighSurrogate(cut[^1]) ? cut[..^1] : cut;
    }

    private readonly record struct LocatedSample(UsageSample Sample, int Day, bool InDays);

    private sealed record HourlySection(DateOnly? From, IReadOnlyList<long> DayStarts, IReadOnlyList<RemoteUsageHourGrid> Grids, int Rows);

    /// <summary>
    /// Maps sample account ids to payload indexes and names the accounts.
    /// Internal ids decide nothing but the order of unnamed accounts.
    /// </summary>
    private sealed class AccountTable
    {
        private readonly Dictionary<string, int> _claudeIndex = new(StringComparer.OrdinalIgnoreCase);
        private int _codexIndex = -1;

        public IReadOnlyList<RemoteUsageAccount> Published { get; private set; } = [];

        public int IndexOf(UsageSample sample) =>
            sample.Provider == UsageProviderKind.Codex ? _codexIndex : _claudeIndex[sample.AccountId ?? string.Empty];

        public static AccountTable Build(
            IReadOnlyList<RemoteUsageAccountSource> sources,
            List<LocatedSample> relevant,
            int maxNameLength)
        {
            var claude = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            var hasCodex = false;
            foreach (var source in sources)
            {
                if (source is null || string.IsNullOrWhiteSpace(source.AccountId))
                {
                    continue;
                }

                if (source.Provider == UsageProviderKind.Codex)
                {
                    hasCodex = true;
                    continue;
                }

                if (!claude.ContainsKey(source.AccountId))
                {
                    var nickname = source.Nickname?.Trim();
                    if (string.IsNullOrEmpty(nickname) ||
                        string.Equals(nickname, source.AccountId.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        nickname = null;
                    }

                    claude[source.AccountId] = new Entry(source.AccountId, nickname, source.Kind);
                }
            }

            foreach (var located in relevant)
            {
                if (located.Sample.Provider == UsageProviderKind.Codex)
                {
                    hasCodex = true;
                }
                else if (!claude.ContainsKey(located.Sample.AccountId ?? string.Empty))
                {
                    // Usage of an account nobody can name any more.
                    claude[located.Sample.AccountId ?? string.Empty] = new Entry(located.Sample.AccountId ?? string.Empty, null, RemoteUsageAccountKind.Removed);
                }
            }

            var ordered = claude.Values
                .OrderBy(entry => entry.SortName(maxNameLength), StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.SortName(maxNameLength), StringComparer.Ordinal)
                .ThenBy(entry => entry.Kind == RemoteUsageAccountKind.Removed ? 1 : 0)
                .ThenBy(entry => entry.AccountId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.AccountId, StringComparer.Ordinal)
                .ToList();

            var table = new AccountTable();
            var published = new List<RemoteUsageAccount>(ordered.Count + 1);
            foreach (var entry in ordered)
            {
                table._claudeIndex[entry.AccountId] = published.Count;
                published.Add(new RemoteUsageAccount(entry.Name(published.Count + 1, maxNameLength), "claude"));
            }

            if (hasCodex)
            {
                table._codexIndex = published.Count;
                published.Add(new RemoteUsageAccount(CodexAccountName, "codex"));
            }

            table.Published = published;
            return table;
        }

        private sealed record Entry(string AccountId, string? Nickname, RemoteUsageAccountKind Kind)
        {
            private string Suffix => Kind == RemoteUsageAccountKind.Removed ? RemovedSuffix : string.Empty;

            /// <summary>Unnamed accounts sort together, as the block their names share.</summary>
            public string SortName(int max) => Kind == RemoteUsageAccountKind.UnknownHistory
                ? UnknownClaudeAccountName
                : Nickname is null ? UnnamedClaudeAccountPrefix : Cut(Nickname, max - Suffix.Length) + Suffix;

            public string Name(int position, int max) => Nickname is null && Kind != RemoteUsageAccountKind.UnknownHistory
                ? string.Create(CultureInfo.InvariantCulture, $"{UnnamedClaudeAccountPrefix} {position}") + Suffix
                : SortName(max);
        }
    }

    /// <summary>
    /// Maps exact model ids to their price and to the payload entry they are
    /// published under, after the length cut.
    /// </summary>
    private sealed class ModelTable
    {
        private readonly Dictionary<(UsageProviderKind Provider, string Model), int> _fullIndex = [];

        public List<FullModel> Full { get; } = [];

        public IReadOnlyList<RemoteUsageModel> Published { get; private set; } = [];

        public int FullIndexOf(UsageSample sample) => _fullIndex[(sample.Provider, ModelOf(sample))];

        public static ModelTable Build(List<LocatedSample> relevant, ModelPricingTable pricing, int maxIdLength)
        {
            var table = new ModelTable();
            var exact = relevant
                .Select(located => (located.Sample.Provider, Model: ModelOf(located.Sample)))
                .Distinct()
                .OrderBy(model => model.Provider)
                .ThenBy(model => model.Model, StringComparer.Ordinal)
                .ToList();

            // The first exact id of each published id, in ordinal order, names
            // and prices the entry.
            var published = exact
                .GroupBy(model => (model.Provider, Id: CutId(model.Model, maxIdLength)))
                .Select(group => (group.Key.Provider, group.Key.Id, Price: pricing.Find(group.First().Model)))
                .OrderBy(model => model.Provider)
                .ThenBy(model => model.Id, StringComparer.Ordinal)
                .ToList();

            var publishedIndex = new Dictionary<(UsageProviderKind, string), int>();
            for (var index = 0; index < published.Count; index++)
            {
                publishedIndex[(published[index].Provider, published[index].Id)] = index;
            }

            foreach (var (provider, model) in exact)
            {
                table._fullIndex[(provider, model)] = table.Full.Count;
                table.Full.Add(new FullModel(publishedIndex[(provider, CutId(model, maxIdLength))], pricing.Find(model)));
            }

            table.Published =
            [
                .. published.Select(model => new RemoteUsageModel(
                    model.Id,
                    ProviderName(model.Provider),
                    model.Price.IsPriced,
                    model.Price.IsPriced ? model.Price : null))
            ];
            return table;
        }

        /// <summary>The aggregator's model rule: a blank id is <c>unknown</c>.</summary>
        private static string ModelOf(UsageSample sample) =>
            string.IsNullOrWhiteSpace(sample.Model) ? "unknown" : sample.Model;

        public sealed record FullModel(int Index, ModelPrice Price);
    }
}
