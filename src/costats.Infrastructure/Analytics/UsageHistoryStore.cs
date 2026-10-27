using System.Globalization;
using System.Text.Json;
using costats.Application.Storage;
using costats.Core.Analytics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace costats.Infrastructure.Analytics;

/// <summary>Name and provider of one account in the usage history.</summary>
public sealed record UsageHistoryAccount(string Key, UsageProviderKind Provider, string Name);

/// <summary>Everything read from the usage history at one moment.</summary>
/// <param name="Buckets">Every stored bucket.</param>
/// <param name="Accounts">Last known name of every account key, by key.</param>
/// <param name="DeletedBefore">
/// Usage before this instant was deleted by the user and must not be shown,
/// even while its logs are still on disk. Null when nothing was deleted.
/// </param>
public sealed record UsageHistorySnapshot(
    IReadOnlyDictionary<UsageHistoryKey, UsageHistoryBucket> Buckets,
    IReadOnlyDictionary<string, UsageHistoryAccount> Accounts,
    DateTimeOffset? DeletedBefore)
{
    /// <summary>No history at all.</summary>
    public static readonly UsageHistorySnapshot Empty = new(
        new Dictionary<UsageHistoryKey, UsageHistoryBucket>(),
        new Dictionary<string, UsageHistoryAccount>(),
        null);
}

/// <summary>Size and span of the stored usage history, for Settings.</summary>
public sealed record UsageHistoryInfo(int MonthCount, long Bytes, DateTimeOffset? FirstBucket)
{
    public static readonly UsageHistoryInfo Empty = new(0, 0, null);
}

/// <summary>
/// Keeps the usage history on disk: one JSON file per UTC month of 15-minute
/// buckets, plus a small state file.
/// </summary>
/// <remarks>
/// <para>
/// Lives in <c>%LOCALAPPDATA%\AIUsageTray\history\</c>, separate from the parse
/// cache, so "Clear cache" never touches it. Only token counts, request counts,
/// model ids, account nicknames and 15-minute timestamps are stored: no message
/// content, paths, session ids or credentials.
/// </para>
/// <para>
/// Every file is written to a temporary file first and swapped in with
/// <see cref="File.Replace(string, string, string?)"/>, which keeps the previous
/// version as <c>.bak</c>. A file that cannot be read falls back to its
/// <c>.bak</c>; when both are unreadable the damaged file is set aside as
/// <c>.corrupt-*</c> rather than overwritten. Writers take a lock file so two
/// app processes never interleave.
/// </para>
/// </remarks>
public sealed class UsageHistoryStore
{
    private const int MonthSchemaVersion = 1;
    private const int StateSchemaVersion = 1;
    private const int RowWidth = 10;
    private const string MonthPrefix = "usage-";
    private const string StateFileName = "state.json";
    private const string LockFileName = "history.lock";
    private const string BackupSuffix = ".bak";

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly ILogger _logger;

    /// <summary>
    /// Creates a store rooted at <paramref name="directory"/>, or at the default
    /// <c>%LOCALAPPDATA%\AIUsageTray\history</c> when null.
    /// </summary>
    public UsageHistoryStore(string? directory = null, ILogger? logger = null)
    {
        Directory = directory ?? DefaultDirectory();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Where the history files live.</summary>
    public string Directory { get; }

    /// <summary>The default history location.</summary>
    public static string DefaultDirectory() => AppDataPaths.Combine("history");

    /// <summary>
    /// True until the one-time recovery of older usage from the parse cache
    /// has run. False for a store whose state had to be repaired, so a repair
    /// can never bring deleted usage back through that recovery.
    /// </summary>
    public bool NeedsCacheRecovery()
    {
        var state = ReadState(out _);
        return !state.CacheRecovered;
    }

    /// <summary>
    /// Every UTC month ("yyyy-MM") that has a history file on disk, whether or
    /// not it can be read right now.
    /// </summary>
    public IReadOnlyList<string> SavedMonths() => ListMonths();

    /// <summary>
    /// Records that a sync had to postpone the cache recovery.
    /// </summary>
    /// <returns>How many syncs have postponed it so far, or null when the lock was busy.</returns>
    public int? RecordPostponedCacheRecovery()
    {
        using var gate = TryLock();
        if (gate is null)
        {
            return null;
        }

        var state = ReadState(out _);
        var updated = state with { RecoveryAttempts = state.RecoveryAttempts + 1 };
        WriteState(updated);
        return updated.RecoveryAttempts;
    }

    /// <summary>Reads every month and the state. Never throws for damaged files.</summary>
    public UsageHistorySnapshot Read()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return UsageHistorySnapshot.Empty;
        }

        var state = ReadState(out _);
        var buckets = new Dictionary<UsageHistoryKey, UsageHistoryBucket>();
        var accounts = new Dictionary<string, UsageHistoryAccount>(StringComparer.Ordinal);

        // Ascending month order, so the newest month's account name wins.
        foreach (var month in ListMonths())
        {
            var file = ReadMonth(month, out _, state);
            if (file is null)
            {
                continue;
            }

            Unpack(file, buckets, accounts);
        }

        return new UsageHistorySnapshot(buckets, accounts, FromUnix(state.DeletedBefore));
    }

    /// <summary>
    /// Merges scanned buckets into the stored months, keeping the larger bucket
    /// for every key, and records the latest account names.
    /// </summary>
    /// <param name="scanned">Buckets from the current logs.</param>
    /// <param name="accounts">Current names of the accounts in <paramref name="scanned"/>.</param>
    /// <param name="cacheRecovered">Marks the one-time cache recovery as done.</param>
    /// <returns>How many buckets were added or replaced, or null when the lock was busy.</returns>
    public int? Merge(
        IReadOnlyDictionary<UsageHistoryKey, UsageHistoryBucket> scanned,
        IReadOnlyCollection<UsageHistoryAccount> accounts,
        bool cacheRecovered)
    {
        ArgumentNullException.ThrowIfNull(scanned);
        ArgumentNullException.ThrowIfNull(accounts);

        using var gate = TryLock();
        if (gate is null)
        {
            return null;
        }

        var state = ReadState(out var stateExisted);
        if (!stateExisted)
        {
            // The state goes first, so a history whose first sync is cut short
            // is never mistaken for one whose state was lost.
            WriteState(state);
        }

        var names = accounts.ToDictionary(account => account.Key, StringComparer.Ordinal);
        var changed = 0;
        var allWritten = true;

        foreach (var monthGroup in scanned.GroupBy(pair => MonthOf(pair.Key.BucketStart)))
        {
            var file = ReadMonth(monthGroup.Key, out var damaged, out var busy, state);
            if (busy)
            {
                // Retried on the next sync; the logs are still on disk meanwhile.
                allWritten = false;
                continue;
            }

            var buckets = new Dictionary<UsageHistoryKey, UsageHistoryBucket>();
            var monthAccounts = new Dictionary<string, UsageHistoryAccount>(StringComparer.Ordinal);
            if (file is not null)
            {
                Unpack(file, buckets, monthAccounts);
            }

            var monthChanged = UsageHistory.MergeLarger(buckets, monthGroup);
            var renamed = false;
            foreach (var key in monthGroup.Select(pair => pair.Key.AccountKey).Distinct(StringComparer.Ordinal))
            {
                if (names.TryGetValue(key, out var current) &&
                    (!monthAccounts.TryGetValue(key, out var stored) || stored != current))
                {
                    monthAccounts[key] = current;
                    renamed = true;
                }
            }

            if (monthChanged == 0 && !renamed)
            {
                continue;
            }

            if (damaged)
            {
                SetAside(MonthPath(monthGroup.Key));
            }

            WriteMonth(monthGroup.Key, buckets, monthAccounts, state.DeletionId);
            changed += monthChanged;
        }

        // The cache recovery counts as done only once everything it found is
        // saved; otherwise it runs again on the next sync.
        if (cacheRecovered && allWritten && !state.CacheRecovered)
        {
            WriteState(state with { CacheRecovered = true });
        }

        return changed;
    }

    /// <summary>
    /// Drops every bucket that starts before <paramref name="cutoff"/>: whole
    /// months are deleted, the month the cutoff falls in is rewritten.
    /// </summary>
    /// <returns>How many buckets were removed, or null when the lock was busy.</returns>
    public int? Prune(DateTimeOffset cutoff)
    {
        using var gate = TryLock();
        if (gate is null)
        {
            return null;
        }

        var state = ReadState(out _);
        var cutoffSeconds = cutoff.ToUnixTimeSeconds();
        var cutoffMonth = MonthOf(cutoffSeconds);
        var removed = 0;

        // Every write happens under this lock, so a temporary file seen here is
        // from a write that stopped halfway; its month is rewritten by the
        // next sync. Damaged copies set aside for an expired month go too.
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, MonthPrefix + "*").ToList())
        {
            var name = Path.GetFileName(path);
            var isCorrupt = name.Contains(".corrupt-", StringComparison.Ordinal);
            if (name.EndsWith(".tmp", StringComparison.Ordinal) ||
                (isCorrupt && name.Length >= MonthPrefix.Length + 7 &&
                 string.CompareOrdinal(name.Substring(MonthPrefix.Length, 7), cutoffMonth) < 0))
            {
                TryDelete(path);
            }
        }

        foreach (var month in ListMonths())
        {
            if (string.CompareOrdinal(month, cutoffMonth) > 0)
            {
                break;
            }

            var file = ReadMonth(month, out var damaged, out var busy, state);
            if (file is null || busy)
            {
                // Unreadable or busy: leave it rather than treating it as
                // empty and deleting it. A later prune retries.
                continue;
            }

            var buckets = new Dictionary<UsageHistoryKey, UsageHistoryBucket>();
            var monthAccounts = new Dictionary<string, UsageHistoryAccount>(StringComparer.Ordinal);
            Unpack(file, buckets, monthAccounts);

            var stale = buckets.Keys.Where(key => key.BucketStart < cutoffSeconds).ToList();
            if (stale.Count == 0)
            {
                // A prune stopped between its two writes leaves the expired
                // usage in .bak only; one more write replaces that copy.
                if (BackupHoldsRowsBefore(month, cutoffSeconds))
                {
                    WriteMonth(month, buckets, monthAccounts, state.DeletionId);
                }

                continue;
            }

            removed += stale.Count;
            if (stale.Count == buckets.Count)
            {
                DeleteMonthFiles(month);
                continue;
            }

            foreach (var key in stale)
            {
                buckets.Remove(key);
            }

            if (damaged)
            {
                SetAside(MonthPath(month));
            }

            // Twice, so the .bak copy does not keep the expired usage either.
            WriteMonth(month, buckets, monthAccounts, state.DeletionId);
            WriteMonth(month, buckets, monthAccounts, state.DeletionId);
        }

        return removed;
    }

    /// <summary>
    /// Deletes all stored history and records that usage before
    /// <paramref name="now"/> must not be shown again, so logs still on disk
    /// cannot bring it back. The deletion point only ever moves forward.
    /// </summary>
    /// <returns>
    /// False when the lock was busy and nothing was deleted, or when some
    /// files could not be removed (they stay hidden behind the deletion point).
    /// </returns>
    public bool DeleteAll(DateTimeOffset now)
    {
        using var gate = TryLock();
        if (gate is null)
        {
            return false;
        }

        // The deletion point is written first: if the app stops halfway, the
        // remaining files are already hidden behind it. One second past now so
        // a request logged in this very second is gone too.
        var state = ReadState(out _);
        var deletedBefore = now.ToUnixTimeSeconds() + 1;
        var deleted = state with
        {
            DeletedBefore = Math.Max(state.DeletedBefore ?? long.MinValue, deletedBefore),
            DeletionId = Guid.NewGuid().ToString("n"),
            CacheRecovered = true
        };

        // Written twice, so the .bak copy carries the deletion point too and a
        // damaged state.json can never fall back to a state from before it.
        WriteState(deleted);
        WriteState(deleted);

        // Every usage file goes: months, backups, set-aside damaged copies and
        // temporary files a stopped write left behind.
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, MonthPrefix + "*").ToList())
        {
            TryDelete(path);
        }

        // A file that could not be deleted stays hidden behind the deletion
        // point, but the caller is told it is still on disk.
        var leftovers = System.IO.Directory.EnumerateFiles(Directory, MonthPrefix + "*").Count();
        if (leftovers > 0)
        {
            _logger.LogWarning("Usage history delete left {Count} files that could not be removed", leftovers);
        }

        return leftovers == 0;
    }

    /// <summary>Size and span of the stored history.</summary>
    public UsageHistoryInfo Inspect()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return UsageHistoryInfo.Empty;
        }

        var months = ListMonths();
        long bytes = 0;
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory))
        {
            try
            {
                bytes += new FileInfo(path).Length;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                // A file swapped during inspection is omitted.
            }
        }

        var state = ReadState(out _);
        DateTimeOffset? first = null;
        foreach (var month in months)
        {
            var file = ReadMonth(month, out _, state);
            if (file is null)
            {
                continue;
            }

            if (EarliestBucket(file) is { } earliest)
            {
                first = DateTimeOffset.FromUnixTimeSeconds(earliest);
                break;
            }
        }

        return new UsageHistoryInfo(months.Count, bytes, first);
    }

    // -- State --------------------------------------------------------------

    private HistoryState ReadState(out bool existed)
    {
        existed = false;
        var path = Path.Combine(Directory, StateFileName);

        // Both copies are read and merged so a deletion point never moves back:
        // the latest deletion and a finished cache recovery win.
        HistoryState? merged = null;
        foreach (var candidate in new[] { path, path + BackupSuffix })
        {
            var state = TryReadJson<HistoryState>(candidate, out _);
            if (state is not { Schema: StateSchemaVersion } ||
                (state.DeletedBefore is { } point && !IsValidInstant(point)))
            {
                continue;
            }

            merged = merged is null
                ? state
                : merged with
                {
                    DeletedBefore = Max(merged.DeletedBefore, state.DeletedBefore),
                    DeletionId = (state.DeletedBefore ?? long.MinValue) > (merged.DeletedBefore ?? long.MinValue)
                        ? state.DeletionId
                        : merged.DeletionId,
                    CacheRecovered = merged.CacheRecovered || state.CacheRecovered,
                    RecoveryAttempts = Math.Max(merged.RecoveryAttempts, state.RecoveryAttempts)
                };
        }

        if (merged is not null)
        {
            existed = true;
            return merged;
        }

        if (!File.Exists(path) && !File.Exists(path + BackupSuffix) && ListMonths().Count == 0)
        {
            // A brand-new history: nothing deleted yet, cache recovery pending.
            return new HistoryState { Schema = StateSchemaVersion };
        }

        // History files exist but the state is gone or damaged. Starting over
        // could bring back usage the user deleted, so repair conservatively:
        // hide everything older than the oldest stored bucket (or everything up
        // to now when no bucket survives) and never rerun the cache recovery.
        var oldest = ListMonths()
            .Select(month => ReadMonth(month, out _))
            .Select(file => file is null ? null : EarliestBucket(file))
            .Min();
        var repaired = new HistoryState
        {
            Schema = StateSchemaVersion,
            DeletedBefore = oldest ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            DeletionId = Guid.NewGuid().ToString("n"),
            CacheRecovered = true
        };
        _logger.LogWarning("Usage history state was missing or damaged and has been repaired; older usage may be hidden");
        return repaired;
    }

    private void WriteState(HistoryState state) =>
        WriteJsonAtomically(Path.Combine(Directory, StateFileName), state);

    // -- Months -------------------------------------------------------------

    private List<string> ListMonths()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        // A month whose main file was set aside before its replacement was
        // written survives only as .bak, so backups name months too.
        return System.IO.Directory.EnumerateFiles(Directory, MonthPrefix + "*")
            .Select(Path.GetFileName)
            .Select(name => name!.EndsWith(".json" + BackupSuffix, StringComparison.Ordinal)
                ? name[..^BackupSuffix.Length]
                : name)
            .Where(name => name.EndsWith(".json", StringComparison.Ordinal))
            .Select(name => name[MonthPrefix.Length..^".json".Length])
            .Where(IsMonthName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Reads one month, falling back to its <c>.bak</c> copy.
    /// <paramref name="damaged"/> is true only when the main file exists and
    /// its content is invalid; a file that merely could not be opened (locked
    /// by a virus scanner, say) sets <paramref name="busy"/> instead, so a
    /// writer skips the month rather than rebuilding it from an older copy.
    /// </summary>
    private MonthFile? ReadMonth(string month, out bool damaged, HistoryState? state = null) =>
        ReadMonth(month, out damaged, out _, state);

    /// <param name="state">
    /// The current state. A month copy not stamped with the latest delete's id
    /// was written before that delete: a leftover the delete could not remove
    /// (locked, or the app stopped halfway). Every bucket starting before the
    /// deletion point is dropped from it, including the one the point falls
    /// in. A copy written after the delete carries its id and keeps that
    /// bucket: it then holds usage saved after the delete.
    /// </param>
    private MonthFile? ReadMonth(string month, out bool damaged, out bool busy, HistoryState? state = null)
    {
        var file = ReadMonthCopy(month, out damaged, out busy);
        return file is not null &&
            state is { DeletedBefore: { } point } &&
            !string.Equals(file.DeletionId, state.DeletionId, StringComparison.Ordinal)
            ? file with { Rows = file.Rows.Where(row => row is { Length: RowWidth } && row[0] >= point).ToArray() }
            : file;
    }

    private MonthFile? ReadMonthCopy(string month, out bool damaged, out bool busy)
    {
        var path = MonthPath(month);
        damaged = false;
        busy = false;
        if (!File.Exists(path) && !File.Exists(path + BackupSuffix))
        {
            return null;
        }

        var main = TryReadJson<MonthFile>(path, out var mainIoFailed);
        if (IsValid(main, month))
        {
            return main;
        }

        // A locked main file is not damaged: readers fall back to the backup
        // copy, writers see busy and leave the month for the next sync.
        damaged = !mainIoFailed && File.Exists(path);
        var backup = TryReadJson<MonthFile>(path + BackupSuffix, out var backupIoFailed);

        // Either copy being locked means the month may hold more than what can
        // be read right now, so writers must leave it alone.
        busy = mainIoFailed || backupIoFailed;
        if (IsValid(backup, month))
        {
            _logger.LogWarning("Usage history for {Month} is read from its backup copy", month);
            return backup;
        }

        _logger.LogWarning("Usage history for {Month} could not be read", month);
        return null;
    }

    private void WriteMonth(
        string month,
        Dictionary<UsageHistoryKey, UsageHistoryBucket> buckets,
        Dictionary<string, UsageHistoryAccount> accounts,
        string? deletionId)
    {
        var accountList = new List<UsageHistoryAccount>();
        var accountIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var models = new List<string>();
        var modelIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<long[]>(buckets.Count);

        foreach (var (key, bucket) in buckets
                     .OrderBy(pair => pair.Key.BucketStart)
                     .ThenBy(pair => pair.Key.AccountKey, StringComparer.Ordinal)
                     .ThenBy(pair => pair.Key.Model, StringComparer.Ordinal))
        {
            if (!accountIndex.TryGetValue(key.AccountKey, out var account))
            {
                account = accountList.Count;
                accountIndex[key.AccountKey] = account;
                accountList.Add(accounts.TryGetValue(key.AccountKey, out var known) && known.Provider == key.Provider
                    ? known
                    : new UsageHistoryAccount(key.AccountKey, key.Provider, key.AccountKey));
            }

            if (!modelIndex.TryGetValue(key.Model, out var model))
            {
                model = models.Count;
                modelIndex[key.Model] = model;
                models.Add(key.Model);
            }

            var tokens = bucket.Tokens;
            rows.Add(
            [
                key.BucketStart,
                account,
                model,
                tokens.UncachedInputTokens,
                tokens.CacheReadInputTokens,
                tokens.CacheWrite5mInputTokens,
                tokens.CacheWrite1hInputTokens,
                tokens.OutputTokens,
                tokens.ReasoningOutputTokens,
                bucket.Requests
            ]);
        }

        WriteJsonAtomically(MonthPath(month), new MonthFile
        {
            Schema = MonthSchemaVersion,
            Month = month,
            DeletionId = deletionId,
            Accounts = accountList
                .Select(account => new MonthAccount
                {
                    Key = account.Key,
                    Provider = account.Provider.ToString(),
                    Name = account.Name
                })
                .ToArray(),
            Models = [.. models],
            Rows = [.. rows]
        });
    }

    private static void Unpack(
        MonthFile file,
        Dictionary<UsageHistoryKey, UsageHistoryBucket> buckets,
        Dictionary<string, UsageHistoryAccount> accounts)
    {
        var parsedAccounts = new UsageHistoryAccount?[file.Accounts.Length];
        for (var i = 0; i < file.Accounts.Length; i++)
        {
            var entry = file.Accounts[i];
            if (entry is null ||
                string.IsNullOrEmpty(entry.Key) ||
                !Enum.TryParse<UsageProviderKind>(entry.Provider, ignoreCase: false, out var provider) ||
                !Enum.IsDefined(provider))
            {
                continue;
            }

            var account = new UsageHistoryAccount(entry.Key, provider, string.IsNullOrWhiteSpace(entry.Name) ? entry.Key : entry.Name);
            parsedAccounts[i] = account;
            accounts[account.Key] = account;
        }

        foreach (var row in file.Rows)
        {
            if (row is not { Length: RowWidth } ||
                row[1] < 0 || row[1] >= parsedAccounts.Length || parsedAccounts[row[1]] is not { } account ||
                row[2] < 0 || row[2] >= file.Models.Length || string.IsNullOrEmpty(file.Models[row[2]]) ||
                row[0] % UsageHistory.BucketSeconds != 0 ||
                row[0] < 0 || row[0] > MaxUnixSeconds - UsageHistory.BucketSeconds ||
                row.Skip(3).Any(value => value < 0))
            {
                continue;
            }

            var key = new UsageHistoryKey(row[0], account.Provider, account.Key, file.Models[row[2]]);
            var bucket = new UsageHistoryBucket(
                new UsageTokens
                {
                    UncachedInputTokens = row[3],
                    CacheReadInputTokens = row[4],
                    CacheWrite5mInputTokens = row[5],
                    CacheWrite1hInputTokens = row[6],
                    OutputTokens = row[7],
                    ReasoningOutputTokens = row[8]
                },
                row[9]);

            // A duplicate row in one file keeps the larger copy, the same rule
            // as every other merge.
            if (!buckets.TryGetValue(key, out var existing) || bucket.IsLargerThan(existing))
            {
                buckets[key] = bucket;
            }
        }
    }

    private bool BackupHoldsRowsBefore(string month, long cutoffSeconds)
    {
        var backup = TryReadJson<MonthFile>(MonthPath(month) + BackupSuffix, out _);
        return IsValid(backup, month) &&
            backup!.Rows.Any(row => row is { Length: RowWidth } && row[0] < cutoffSeconds);
    }

    /// <summary>Start of the earliest valid bucket in a month file, or null when it has none.</summary>
    private static long? EarliestBucket(MonthFile file)
    {
        var buckets = new Dictionary<UsageHistoryKey, UsageHistoryBucket>();
        Unpack(file, buckets, new Dictionary<string, UsageHistoryAccount>(StringComparer.Ordinal));
        return buckets.Count == 0 ? null : buckets.Keys.Min(key => key.BucketStart);
    }

    private static bool IsValid(MonthFile? file, string month) =>
        file is { Schema: MonthSchemaVersion } &&
        string.Equals(file.Month, month, StringComparison.Ordinal) &&
        file.Accounts is not null &&
        file.Models is not null &&
        file.Rows is not null;

    private void DeleteMonthFiles(string month)
    {
        var path = MonthPath(month);
        TryDelete(path);
        TryDelete(path + BackupSuffix);
    }

    private string MonthPath(string month) => Path.Combine(Directory, MonthPrefix + month + ".json");

    /// <summary>"yyyy-MM" of the UTC month a bucket starts in.</summary>
    public static string MonthOf(long bucketStartUnixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(bucketStartUnixSeconds).UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static bool IsMonthName(string value) =>
        DateTime.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    // -- Files --------------------------------------------------------------

    /// <summary>
    /// Takes the cross-process write lock, waiting up to ten seconds. Returns
    /// null when another writer holds it; callers skip their write and try
    /// again on the next cycle rather than writing unlocked.
    /// </summary>
    private FileStream? TryLock()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, LockFileName);
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
            catch (IOException)
            {
                _logger.LogWarning("Usage history is busy in another process; skipping this update");
                return null;
            }
        }
    }

    private static T? TryReadJson<T>(string path, out bool ioFailed)
        where T : class
    {
        ioFailed = false;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, SerializerOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ioFailed = true;
            return null;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Writes <paramref name="value"/> to a temporary file, flushes it to disk
    /// and swaps it in, keeping the previous version as <c>.bak</c>.
    /// </summary>
    private static void WriteJsonAtomically<T>(string path, T value)
    {
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, SerializerOptions);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            File.Replace(temp, path, path + BackupSuffix, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    /// <summary>Keeps an unreadable file for inspection instead of overwriting it.</summary>
    private void SetAside(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Move(path, path[..^".json".Length] + ".corrupt-" +
                    DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".json");
            }
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            _logger.LogWarning("A damaged usage history file could not be set aside: {Error}", exception.GetType().Name);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // A file that cannot be deleted now is hidden by the deletion point
            // and removed on a later delete.
        }
    }

    private static DateTimeOffset? FromUnix(long? seconds) =>
        seconds is { } value ? DateTimeOffset.FromUnixTimeSeconds(value) : null;

    private static readonly long MinUnixSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
    private static readonly long MaxUnixSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    /// <summary>True when unix seconds fit in a <see cref="DateTimeOffset"/>.</summary>
    private static bool IsValidInstant(long seconds) => seconds >= MinUnixSeconds && seconds <= MaxUnixSeconds;

    private static long? Max(long? left, long? right) =>
        left is null ? right : right is null ? left : Math.Max(left.Value, right.Value);

    private static bool IsRecoverable(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException
            or ArgumentException;

    private sealed record HistoryState
    {
        public int Schema { get; init; }

        /// <summary>Unix seconds; usage before this was deleted by the user.</summary>
        public long? DeletedBefore { get; init; }

        /// <summary>True once the one-time recovery from the parse cache has run.</summary>
        public bool CacheRecovered { get; init; }

        /// <summary>Syncs that had to postpone the cache recovery so far.</summary>
        public int RecoveryAttempts { get; init; }

        /// <summary>
        /// Random id of the latest delete; month files carry the id they were
        /// written under. Random rather than counted, so a repaired state can
        /// never reuse an id that an older file still carries.
        /// </summary>
        public string? DeletionId { get; init; }
    }

    /// <summary>
    /// On-disk month. Rows are positional so a busy month stays small:
    /// <c>[bucketStartUnixSeconds, accountIndex, modelIndex, uncachedInput,
    /// cacheRead, cacheWrite5m, cacheWrite1h, output, reasoning, requests]</c>.
    /// </summary>
    private sealed record MonthFile
    {
        public int Schema { get; init; }
        public string Month { get; init; } = string.Empty;

        /// <summary>The state's delete id when this file was written.</summary>
        public string? DeletionId { get; init; }
        public MonthAccount[] Accounts { get; init; } = [];
        public string[] Models { get; init; } = [];
        public long[][] Rows { get; init; } = [];
    }

    private sealed record MonthAccount
    {
        public string Key { get; init; } = string.Empty;
        public string Provider { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
    }
}
