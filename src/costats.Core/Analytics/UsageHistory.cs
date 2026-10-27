namespace costats.Core.Analytics;

/// <summary>
/// Identifies one usage-history bucket: a 15-minute UTC slot of one model on
/// one account.
/// </summary>
/// <param name="BucketStart">Unix seconds of the slot start, a multiple of <see cref="UsageHistory.BucketSeconds"/>.</param>
/// <param name="Provider">Which agent produced the usage.</param>
/// <param name="AccountKey">
/// The account's history key from <see cref="UsageHistory"/>, not its settings
/// id: settings ids are reused after an account is removed, history keys are not.
/// </param>
/// <param name="Model">Model id, with a blank id stored as <c>unknown</c>.</param>
public readonly record struct UsageHistoryKey(
    long BucketStart,
    UsageProviderKind Provider,
    string AccountKey,
    string Model);

/// <summary>Summed tokens and requests of one usage-history bucket.</summary>
public readonly record struct UsageHistoryBucket(UsageTokens Tokens, long Requests)
{
    /// <summary>
    /// True when this bucket holds more usage than <paramref name="other"/>,
    /// compared by processed tokens and then by request count. A tie is not
    /// larger, so live log data wins it and keeps its exact timestamps.
    /// </summary>
    public bool IsLargerThan(UsageHistoryBucket other) =>
        Tokens.ProcessedTokens != other.Tokens.ProcessedTokens
            ? Tokens.ProcessedTokens > other.Tokens.ProcessedTokens
            : Requests > other.Requests;

    /// <summary>Adds one sample's usage.</summary>
    public UsageHistoryBucket Add(UsageTokens tokens, long requests) =>
        new(Tokens + tokens, Requests + requests);
}

/// <summary>
/// The pure rules of the local usage history: how log samples become history
/// buckets, how a new scan is merged into the stored history, and how the two
/// are combined for a report. No IO.
/// </summary>
/// <remarks>
/// Agent logs disappear (Claude Code deletes transcripts after 30 days by
/// default), so the app keeps a copy of their usage in 15-minute buckets. The
/// one merge rule everywhere is "the larger bucket wins", never "add both":
/// <list type="bullet">
/// <item>While a log still exists, its bucket is at least as large as the
/// stored copy, so the live data is used with its exact timestamps.</item>
/// <item>Once a log is deleted, or a scan could not read it, the live bucket
/// shrinks and the stored copy takes over.</item>
/// <item>Usage is never counted twice, because the two sources are never summed.</item>
/// </list>
/// Fifteen minutes is the finest step every real time zone offset is a multiple
/// of, so local days and local hours rebuilt from buckets match the desktop's
/// own day and hour boundaries in every zone.
/// </remarks>
public static class UsageHistory
{
    /// <summary>Length of one bucket, in seconds.</summary>
    public const long BucketSeconds = 15 * 60;

    /// <summary>History key of the merged Codex account, shared by every Codex profile.</summary>
    public const string CodexAccountKey = UsageAccounts.MergedCodexId;

    /// <summary>
    /// History key for Claude usage recovered from the parse cache whose
    /// account can no longer be told.
    /// </summary>
    public const string UnknownClaudeAccountKey = "claude:unknown";

    /// <summary>Display name for <see cref="UnknownClaudeAccountKey"/>.</summary>
    public const string UnknownClaudeAccountName = "Earlier Claude history (account unknown)";

    /// <summary>Prefix of the account id given to history whose account is no longer configured.</summary>
    public const string ArchivedAccountIdPrefix = "archived:";

    /// <summary>Unix seconds of the bucket that contains <paramref name="instant"/>.</summary>
    public static long BucketStartOf(DateTimeOffset instant)
    {
        var seconds = instant.ToUnixTimeSeconds();
        var offset = seconds % BucketSeconds;
        return offset < 0 ? seconds - offset - BucketSeconds : seconds - offset;
    }

    /// <summary>The model id a bucket is stored under.</summary>
    public static string NormalizeModel(string? model) =>
        string.IsNullOrWhiteSpace(model) ? "unknown" : model;

    /// <summary>
    /// Folds samples into buckets.
    /// </summary>
    /// <param name="samples">Deduplicated samples.</param>
    /// <param name="accountKeyOf">
    /// Maps a sample's account id to its history key. Samples it maps to null
    /// are skipped.
    /// </param>
    /// <param name="notBefore">When set, samples earlier than this instant are skipped.</param>
    public static Dictionary<UsageHistoryKey, UsageHistoryBucket> Bucketize(
        IEnumerable<UsageSample> samples,
        Func<string, string?> accountKeyOf,
        DateTimeOffset? notBefore = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(accountKeyOf);

        var buckets = new Dictionary<UsageHistoryKey, UsageHistoryBucket>();
        foreach (var sample in samples)
        {
            if (sample is null || (notBefore is { } floor && sample.Timestamp < floor))
            {
                continue;
            }

            var key = KeyOf(sample, accountKeyOf);
            if (key is not { } bucketKey)
            {
                continue;
            }

            buckets[bucketKey] = buckets.TryGetValue(bucketKey, out var existing)
                ? existing.Add(sample.Tokens, sample.Requests)
                : new UsageHistoryBucket(sample.Tokens, sample.Requests);
        }

        return buckets;
    }

    /// <summary>
    /// Merges freshly scanned buckets into stored history, keeping the larger
    /// bucket for every key.
    /// </summary>
    /// <returns>How many stored buckets were added or replaced.</returns>
    public static int MergeLarger(
        IDictionary<UsageHistoryKey, UsageHistoryBucket> history,
        IEnumerable<KeyValuePair<UsageHistoryKey, UsageHistoryBucket>> scanned)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(scanned);

        var changed = 0;
        foreach (var (key, bucket) in scanned)
        {
            if (!history.TryGetValue(key, out var stored) || bucket.IsLargerThan(stored))
            {
                history[key] = bucket;
                changed++;
            }
        }

        return changed;
    }

    /// <summary>
    /// Combines live samples with stored history into the samples a report is
    /// built from. For each bucket the larger source wins: live samples keep
    /// their exact timestamps, and a stored bucket that wins becomes one
    /// sample at the bucket start carrying the bucket's request count.
    /// </summary>
    /// <param name="live">Deduplicated samples from the logs on disk.</param>
    /// <param name="accountKeyOf">Maps a live sample's account id to its history key.</param>
    /// <param name="history">Stored buckets.</param>
    /// <param name="accountIdOf">Maps a history key to the account id reports use.</param>
    /// <param name="notBefore">
    /// When set, every sample earlier than this instant is dropped, from either
    /// source. This is how deleted and expired history stays gone even while
    /// its logs are still on disk.
    /// </param>
    /// <returns>Samples ascending by timestamp.</returns>
    public static List<UsageSample> Combine(
        IReadOnlyList<UsageSample> live,
        Func<string, string?> accountKeyOf,
        IReadOnlyDictionary<UsageHistoryKey, UsageHistoryBucket> history,
        Func<string, string> accountIdOf,
        DateTimeOffset? notBefore)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(accountKeyOf);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(accountIdOf);

        // Compared on the visible part only: live usage from before the floor
        // is dropped anyway and must not outweigh history saved after it.
        var liveBuckets = Bucketize(live, accountKeyOf, notBefore);
        var historyWins = new HashSet<UsageHistoryKey>();
        var combined = new List<UsageSample>(live.Count + history.Count);

        foreach (var (key, bucket) in history)
        {
            if (liveBuckets.TryGetValue(key, out var liveBucket) && !bucket.IsLargerThan(liveBucket))
            {
                continue;
            }

            historyWins.Add(key);
            var timestamp = DateTimeOffset.FromUnixTimeSeconds(key.BucketStart);
            if (notBefore is { } floor && timestamp < floor)
            {
                // A bucket that ends after the floor can hold usage from after
                // it: the minutes right after a delete are saved into the same
                // 15-minute bucket. It is kept whole, dated at the floor so it
                // stays inside the visible range. At a retention cutoff this
                // keeps at most 15 minutes of older usage, which is harmless.
                if (key.BucketStart + BucketSeconds <= floor.ToUnixTimeSeconds())
                {
                    continue;
                }

                timestamp = floor;
            }

            combined.Add(new UsageSample(timestamp, key.Provider, accountIdOf(key.AccountKey), key.Model, bucket.Tokens)
            {
                Requests = bucket.Requests
            });
        }

        foreach (var sample in live)
        {
            if (notBefore is { } floor && sample.Timestamp < floor)
            {
                continue;
            }

            if (historyWins.Count > 0 && KeyOf(sample, accountKeyOf) is { } key && historyWins.Contains(key))
            {
                continue;
            }

            combined.Add(sample);
        }

        combined.Sort(static (left, right) => left.Timestamp.CompareTo(right.Timestamp));
        return combined;
    }

    private static UsageHistoryKey? KeyOf(UsageSample sample, Func<string, string?> accountKeyOf)
    {
        var accountKey = accountKeyOf(sample.AccountId);
        return accountKey is null
            ? null
            : new UsageHistoryKey(BucketStartOf(sample.Timestamp), sample.Provider, accountKey, NormalizeModel(sample.Model));
    }
}
