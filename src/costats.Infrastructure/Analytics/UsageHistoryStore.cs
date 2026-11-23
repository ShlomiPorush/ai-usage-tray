using System.Globalization;
using System.Text.Json;
using costats.Application.Storage;
using costats.Core.Analytics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace costats.Infrastructure.Analytics;

public sealed record UsageHistoryAccount(string Key, UsageProviderKind Provider, string Name);

/// <param name="DeletedBefore">
/// Usage before this instant was deleted by the user and must not be shown,
/// even while its logs are still on disk. Null when nothing was deleted.
/// </param>
public sealed record UsageHistorySnapshot(
    IReadOnlyDictionary<UsageHistoryKey, UsageHistoryBucket> Buckets,
    IReadOnlyDictionary<string, UsageHistoryAccount> Accounts,
    DateTimeOffset? DeletedBefore)
{
    public static readonly UsageHistorySnapshot Empty = new(
        new Dictionary<UsageHistoryKey, UsageHistoryBucket>(),
        new Dictionary<string, UsageHistoryAccount>(),
        null);
}

/// <param name="Bytes">Size of every file in the history folder, not only the months.</param>
/// <param name="FirstBucket">The earliest saved usage, or null when none is shown.</param>
public sealed record UsageHistoryInfo(int MonthCount, long Bytes, DateTimeOffset? FirstBucket)
{
    public static readonly UsageHistoryInfo Empty = new(0, 0, null);
}

/// <summary>
/// The deletion point a sync scans against. A sync whose point changed by the
/// time it writes writes nothing.
/// </summary>
/// <param name="DeletionId">The state's deletion id, after any repair was saved.</param>
internal sealed record HistorySyncStart(string? DeletionId, DateTimeOffset? DeletedBefore);

internal sealed record ClaudeConversion(long FreezeMs, IReadOnlyList<ClaudeRequestRow> Marks);

/// <param name="Names">Current names of the accounts in <paramref name="Buckets"/>.</param>
/// <param name="StoreNames">Current names of the accounts the request store holds rows for.</param>
/// <param name="Conversion">Null when this sync may not perform the conversion.</param>
internal sealed record HistoryWritePlan(
    IReadOnlyDictionary<UsageHistoryKey, UsageHistoryBucket> Buckets,
    IReadOnlyCollection<UsageHistoryAccount> Names,
    bool CacheRecovered,
    IReadOnlyList<ClaudeRequestRow> Records,
    IReadOnlyCollection<UsageHistoryAccount> StoreNames,
    ClaudeConversion? Conversion);

/// <param name="Changed">Buckets added or replaced.</param>
/// <param name="Skipped">True when a delete happened since the sync started, so nothing was written.</param>
/// <param name="NewestRecordMs">The newest record's timestamp, for the clock check before a prune.</param>
internal sealed record HistorySaveOutcome(int Changed, int RecordsAdded, bool Converted, bool Skipped, long? NewestRecordMs);

/// <param name="Snapshot">
/// <see cref="UsageHistorySnapshot.Accounts"/> includes the request store's names.
/// </param>
internal sealed record HistoryReportRead(UsageHistorySnapshot Snapshot, ClaudeRequestView Claude);

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
/// <para>
/// Claude requests with an identity are also kept in
/// <c>claude-requests.db</c>, a SQLite file beside the months (see
/// <see cref="UsageHistory"/>): marks for the requests the one-time conversion
/// found in the logs, records for every request logged after it. It stores
/// only the identity hash, timestamp, bucket, account key, model and token
/// counts, plus account names. Every connection to it is opened and closed
/// while holding the lock file, so a restore or a delete never meets an open
/// connection. Its records exist nowhere else once Claude Code deletes their
/// logs, so every sync copies it to <c>.bak</c>, and a damaged store is
/// replaced by that copy.
/// </para>
/// <para>
/// Deleting the history first saves the new deletion point and id in the
/// state, then removes every other file. Reports hide everything before the
/// point, so a file a delete could not remove shows nothing it deleted. A
/// lost state is rebuilt from the request store or the month files' stamps,
/// never from a guess, and an unknown deletion id never leads to removing
/// anything.
/// </para>
/// <para>
/// What it guarantees: a Claude request with an identity that is logged at or
/// after the conversion is counted once and never lost, whatever happens to
/// its log files. Usage from before the conversion is never counted lower than
/// before, and requests still in the logs at the conversion stop doubling when
/// their logs move. Requests without an identity, Codex and Antigravity are
/// unchanged.
/// </para>
/// <para>
/// Limits: an older app version run after the conversion ignores the store and
/// can count records again; a request logged before the conversion that turns
/// up only after it (for example a log restored into another account) follows
/// the old rules and can still be counted twice; duplicates already in the
/// history stay; when the store and its backup are both damaged, its requests
/// are left out of reports and new ones are shown from the logs but not saved
/// until the damaged files are removed; requests saved in a sync whose backup
/// did not finish are lost if the store is damaged before the next backup and
/// their logs are gone by then.
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

    // A month file from an older version has no stamp and reads as 0: it
    // carries the deletion id but not the point.
    private const int MonthStampOldVersion = 0;
    private const int MonthStampKnown = 1;
    private const int MonthStampUnknown = 2;
    private const string BackupSuffix = ".bak";

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InspectLockTimeout = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly ILogger _logger;
    private readonly ClaudeRequestStore _claude;

    public UsageHistoryStore(string? directory = null, ILogger? logger = null)
    {
        Directory = directory ?? DefaultDirectory();
        _logger = logger ?? NullLogger.Instance;
        _claude = new ClaudeRequestStore(Directory, _logger);
    }

    public string Directory { get; }

    internal TimeSpan ReportLockTimeout { get; init; } = LockTimeout;

    public static string DefaultDirectory() => AppDataPaths.Combine("history");

    /// <summary>
    /// False for a store whose state had to be repaired, so a repair can never
    /// bring deleted usage back through the cache recovery.
    /// </summary>
    public bool NeedsCacheRecovery()
    {
        var state = ReadState(out _, out _);
        return !state.CacheRecovered;
    }

    /// <summary>Includes months whose files cannot be read right now.</summary>
    public IReadOnlyList<string> SavedMonths() => ListMonths();

    /// <returns>How many syncs have postponed it so far, or null when the lock was busy.</returns>
    public int? RecordPostponedCacheRecovery()
    {
        using var gate = TryLock();
        if (gate is null)
        {
            return null;
        }

        using var claude = OpenLocked(out var state, out _, out _);
        var updated = state with { RecoveryAttempts = state.RecoveryAttempts + 1 };
        WriteState(updated);
        return updated.RecoveryAttempts;
    }

    /// <summary>Never throws for damaged files.</summary>
    public UsageHistorySnapshot Read()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return UsageHistorySnapshot.Empty;
        }

        var state = ReadState(out _, out _);
        return ReadSnapshot(state, EffectiveDeletedBefore(state));
    }

    private UsageHistorySnapshot ReadSnapshot(HistoryState state, DateTimeOffset? deletedBefore)
    {
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

        return new UsageHistorySnapshot(buckets, accounts, deletedBefore);
    }

    /// <summary>Keeps the larger of the stored and scanned bucket for every key.</summary>
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

        using var claude = OpenLocked(out var state, out var stateExisted, out _);
        if (!stateExisted)
        {
            // The state goes first, so a history whose first sync is cut short
            // is never mistaken for one whose state was lost.
            WriteState(state);
        }

        var merged = MergeLocked(state, scanned, accounts);
        if (cacheRecovered && merged.AllWritten && !state.CacheRecovered)
        {
            WriteState(state with { CacheRecovered = true });
        }

        return merged.Changed;
    }

    /// <summary>
    /// Saves a repaired state, so the deletion id that <see cref="Save"/>
    /// compares against stays the same from now on.
    /// </summary>
    /// <returns>Null when the lock was busy; the sync then skips this round.</returns>
    internal HistorySyncStart? BeginSync()
    {
        using var gate = TryLock();
        if (gate is null)
        {
            return null;
        }

        using var claude = OpenLocked(out var state, out _, out var repaired);
        if (repaired)
        {
            WriteState(state);
        }

        return new HistorySyncStart(state.DeletionId, EffectiveDeletedBefore(state));
    }

    /// <summary>
    /// <paramref name="plan"/> runs under the same hold of the lock as the
    /// writes, so it plans from the request store the writes go to.
    /// </summary>
    /// <param name="start">A delete since the sync began makes this write nothing.</param>
    /// <returns>Null when the lock was busy.</returns>
    internal HistorySaveOutcome? Save(HistorySyncStart start, Func<ClaudeRequestView, HistoryWritePlan> plan)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(plan);

        using var gate = TryLock();
        if (gate is null)
        {
            return null;
        }

        var claude = OpenLocked(out var state, out var stateExisted, out _);
        try
        {
            if (!string.Equals(state.DeletionId, start.DeletionId, StringComparison.Ordinal) ||
                EffectiveDeletedBefore(state) != start.DeletedBefore)
            {
                // The history was deleted after this sync read the logs: what
                // it holds may be usage the user just deleted.
                _logger.LogInformation("Usage history: deleted while a sync was reading the logs; nothing saved");
                return new HistorySaveOutcome(0, 0, false, true, null);
            }

            if (!stateExisted)
            {
                WriteState(state);
            }

            var requests = claude.ReadRequests();
            var view = claude.Status switch
            {
                ClaudeStoreStatus.Available => new ClaudeRequestView(ClaudeStoreStatus.Available, claude.Meta!.FreezeMs, requests, claude.ReadAccounts()),
                ClaudeStoreStatus.Unavailable => ClaudeRequestView.Unavailable(state.ClaudeFreezeMs),
                _ => ClaudeRequestView.Absent
            };
            var write = plan(view);

            var merged = MergeLocked(state, write.Buckets, write.Names);
            if (write.CacheRecovered && merged.AllWritten && !state.CacheRecovered)
            {
                state = state with { CacheRecovered = true };
                WriteState(state);
            }

            var added = 0;
            var converted = false;
            if (claude.Status == ClaudeStoreStatus.Available)
            {
                var names = write.StoreNames
                    .Where(name => !view.Accounts.TryGetValue(name.Key, out var stored) || stored != name)
                    .ToList();
                added = claude.AddRecords(write.Records, names);
            }
            else if (claude.Status == ClaudeStoreStatus.Absent &&
                     write.Conversion is { } conversion &&
                     state.CacheRecovered &&
                     !state.DeletionUnknown &&
                     !write.Buckets.Keys.Any(key =>
                         key.Provider == UsageProviderKind.Claude && merged.BusyMonths.Contains(MonthOf(key.BucketStart))))
            {
                // Every month with Claude usage holds this scan's buckets now,
                // so each mark points at a bucket that really counts it.
                var meta = new ClaudeStoreMeta(conversion.FreezeMs, state.DeletionId, state.DeletedBefore * 1000);
                if (_claude.Create(meta, [.. conversion.Marks], write.StoreNames))
                {
                    converted = true;
                    state = state with { ClaudeFreezeMs = conversion.FreezeMs };
                    WriteState(state);
                    claude.Dispose();
                    claude = _claude.Open();
                    _logger.LogInformation(
                        "Usage history: Claude requests are now kept by identity ({Marks} requests marked as already saved)",
                        conversion.Marks.Count);
                }
            }

            claude.Backup();

            long? newest = null;
            foreach (var row in requests.Values.Concat(write.Records))
            {
                if (!row.IsMark && (newest is null || row.TimestampMs > newest))
                {
                    newest = row.TimestampMs;
                }
            }

            return new HistorySaveOutcome(merged.Changed, added, converted, false, newest);
        }
        finally
        {
            claude.Dispose();
        }
    }

    /// <summary>
    /// When the lock is busy, the month files are read without it and the
    /// request store is left out.
    /// </summary>
    internal HistoryReportRead ReadForReport()
    {
        using var gate = TryLock(ReportLockTimeout, quiet: true);
        if (gate is null)
        {
            _logger.LogDebug("Usage history is busy; reporting without the saved Claude requests");
            var unlocked = ReadState(out _, out _);
            return new HistoryReportRead(
                ReadSnapshot(unlocked, EffectiveDeletedBefore(unlocked)),
                _claude.FilesPresent ? ClaudeRequestView.Unavailable(unlocked.ClaudeFreezeMs) : ClaudeRequestView.Absent);
        }

        using var claude = OpenLocked(out var state, out _, out _);
        var snapshot = ReadSnapshot(state, EffectiveDeletedBefore(state));
        switch (claude.Status)
        {
            case ClaudeStoreStatus.Available:
                var storeAccounts = claude.ReadAccounts();
                var accounts = new Dictionary<string, UsageHistoryAccount>(snapshot.Accounts, StringComparer.Ordinal);
                foreach (var (key, account) in storeAccounts)
                {
                    accounts[key] = account;
                }

                return new HistoryReportRead(
                    snapshot with { Accounts = accounts },
                    new ClaudeRequestView(ClaudeStoreStatus.Available, claude.Meta!.FreezeMs, claude.ReadRequests(), storeAccounts));

            case ClaudeStoreStatus.Unavailable:
                return new HistoryReportRead(snapshot, ClaudeRequestView.Unavailable(state.ClaudeFreezeMs));

            default:
                return new HistoryReportRead(snapshot, ClaudeRequestView.Absent);
        }
    }

    private readonly record struct MergeResult(int Changed, bool AllWritten, IReadOnlySet<string> BusyMonths);

    private MergeResult MergeLocked(
        HistoryState state,
        IReadOnlyDictionary<UsageHistoryKey, UsageHistoryBucket> scanned,
        IReadOnlyCollection<UsageHistoryAccount> accounts)
    {
        var names = accounts.ToDictionary(account => account.Key, StringComparer.Ordinal);
        var changed = 0;
        var allWritten = true;
        var busyMonths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var monthGroup in scanned.GroupBy(pair => MonthOf(pair.Key.BucketStart)))
        {
            var file = ReadMonth(monthGroup.Key, out var damaged, out var busy, state);
            if (busy)
            {
                // Retried on the next sync; the logs are still on disk meanwhile.
                allWritten = false;
                busyMonths.Add(monthGroup.Key);
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

            WriteMonth(monthGroup.Key, buckets, monthAccounts, state);
            changed += monthChanged;
        }

        // Callers mark the cache recovery done only when AllWritten, so it
        // runs again until everything it found is saved.
        return new MergeResult(changed, allWritten, busyMonths);
    }

    /// <remarks>
    /// Claude requests before the cutoff go too, but nothing from the earliest
    /// month that could not be pruned onwards, so a mark never goes while its
    /// bucket stays.
    /// </remarks>
    /// <returns>How many buckets and requests were removed, or null when the lock was busy.</returns>
    public int? Prune(DateTimeOffset cutoff)
    {
        using var gate = TryLock();
        if (gate is null)
        {
            return null;
        }

        using var claude = OpenLocked(out var state, out _, out _);
        var cutoffSeconds = cutoff.ToUnixTimeSeconds();
        var removedMonths = PruneMonths(state, cutoff, out var keptFrom);
        var removedRequests = 0;
        if (claude.Status == ClaudeStoreStatus.Available)
        {
            try
            {
                var floorSeconds = keptFrom is { } kept ? Math.Min(cutoffSeconds, kept) : cutoffSeconds;
                removedRequests = claude.Prune(floorSeconds * 1000);
            }
            catch (Exception exception) when (ClaudeRequestStore.IsRecoverable(exception))
            {
                _logger.LogWarning("Usage history: expired Claude requests could not be removed: {Error}", exception.GetType().Name);
            }
        }

        return removedMonths + removedRequests;
    }

    /// <param name="keptFrom">
    /// Unix seconds of the start of the earliest month that should have been
    /// pruned but could not be (busy, unreadable, or its file could not be
    /// deleted), or null when every such month was pruned.
    /// </param>
    private int PruneMonths(HistoryState state, DateTimeOffset cutoff, out long? keptFrom)
    {
        keptFrom = null;
        var cutoffSeconds = cutoff.ToUnixTimeSeconds();
        var cutoffMonth = MonthOf(cutoffSeconds);
        var removed = 0;

        // Every write happens under this lock, so a temporary file seen here is
        // from a write that stopped halfway and is safe to delete.
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
                keptFrom ??= MonthStart(month);
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
                    WriteMonth(month, buckets, monthAccounts, state);
                }

                continue;
            }

            removed += stale.Count;
            if (stale.Count == buckets.Count)
            {
                DeleteMonthFiles(month);
                if (File.Exists(MonthPath(month)) || File.Exists(MonthPath(month) + BackupSuffix))
                {
                    keptFrom ??= MonthStart(month);
                }

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
            WriteMonth(month, buckets, monthAccounts, state);
            WriteMonth(month, buckets, monthAccounts, state);
        }

        return removed;
    }

    /// <summary>
    /// Usage before <paramref name="now"/> stays hidden afterwards, so logs
    /// still on disk cannot bring it back. The deletion point only moves forward.
    /// </summary>
    /// <returns>
    /// False when the lock was busy or the state could not be written, or when
    /// some files could not be removed; those stay hidden behind the new
    /// deletion point.
    /// </returns>
    public bool DeleteAll(DateTimeOffset now)
    {
        using var gate = TryLock();
        if (gate is null)
        {
            return false;
        }

        try
        {
            var state = ReadState(out _, out _);

            // One second past now so a request logged in this very second is gone too.
            var point = Math.Max(EffectiveDeletedBefore(state)?.ToUnixTimeSeconds() ?? long.MinValue, now.ToUnixTimeSeconds() + 1);

            // The state goes first: from here on the deleted usage is hidden,
            // whatever happens to the files below.
            WriteState(new HistoryState
            {
                Schema = StateSchemaVersion,
                DeletedBefore = point,
                DeletionId = Guid.NewGuid().ToString("n"),
                CacheRecovered = true
            });

            string[] kept = [StateFileName, StateFileName + BackupSuffix, LockFileName];
            foreach (var path in System.IO.Directory.EnumerateFiles(Directory).ToList())
            {
                if (!kept.Contains(Path.GetFileName(path)))
                {
                    TryDelete(path);
                }
            }

            if (System.IO.Directory.EnumerateFiles(Directory).All(path => kept.Contains(Path.GetFileName(path))))
            {
                return true;
            }

            _logger.LogWarning("Usage history: some files could not be deleted; the deleted usage stays hidden");
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Usage history could not be deleted: {Error}", exception.GetType().Name);
            return false;
        }
    }

    private static long MonthStart(string month) =>
        new DateTimeOffset(
            DateTime.ParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            TimeSpan.Zero).ToUnixTimeSeconds();

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

        // Settings does not wait long for the lock; when busy, it shows the
        // month files alone.
        long? earliestRecordMs = null;
        HistoryState? state = null;
        if (_claude.FilesPresent)
        {
            using var gate = TryLock(InspectLockTimeout, quiet: true);
            if (gate is not null)
            {
                using var claude = _claude.Open(restore: false);
                state = ReadState(out _, out _, claude.Meta);
                try
                {
                    earliestRecordMs = claude.EarliestRecordMs(EffectiveDeletedBefore(state)?.ToUnixTimeMilliseconds());
                }
                catch (Exception exception) when (ClaudeRequestStore.IsRecoverable(exception))
                {
                    earliestRecordMs = null;
                }
            }
        }

        state ??= ReadState(out _, out _);
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

        if (earliestRecordMs is { } recordMs)
        {
            var record = DateTimeOffset.FromUnixTimeMilliseconds(recordMs);
            if (first is null || record < first)
            {
                first = record;
            }
        }

        return new UsageHistoryInfo(months.Count, bytes, first);
    }

    /// <summary>
    /// The caller holds the lock. A state rebuilt without knowing its deletion
    /// id takes it from the request store once that can be read.
    /// </summary>
    private ClaudeStoreSession OpenLocked(out HistoryState state, out bool existed, out bool repaired)
    {
        SyncStateBackup();
        var claude = _claude.Open();
        state = ReadState(out existed, out repaired, claude.Meta);
        if (state.DeletionUnknown && claude.Meta is { } meta)
        {
            _logger.LogWarning("Usage history: a rebuilt state takes its deletion point from the Claude request store");
            state = state with
            {
                DeletedBefore = meta.DeletedBeforeMs is { } point && IsValidInstant(CeilingSeconds(point)) ? CeilingSeconds(point) : null,
                DeletionId = meta.DeletionId,
                DeletionUnknown = false,
                RepairFloor = null,
                ClaudeFreezeMs = meta.FreezeMs
            };
            WriteState(state);
            RestampUnknownMonths(state);
            existed = true;
            repaired = false;
        }

        return claude;
    }

    /// <summary>
    /// Keeps all of each month's usage. A month that cannot be rewritten now
    /// keeps its unknown stamp and stays whole.
    /// </summary>
    private void RestampUnknownMonths(HistoryState state)
    {
        foreach (var month in ListMonths())
        {
            var file = TryReadJson<MonthFile>(MonthPath(month), out _);
            if (!IsValid(file, month) || file!.Stamp != MonthStampUnknown)
            {
                continue;
            }

            var buckets = new Dictionary<UsageHistoryKey, UsageHistoryBucket>();
            var accounts = new Dictionary<string, UsageHistoryAccount>(StringComparer.Ordinal);
            Unpack(file, buckets, accounts);
            try
            {
                WriteMonth(month, buckets, accounts, state);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Usage history for {Month} could not be restamped yet: {Error}", month, exception.GetType().Name);
            }
        }
    }

    /// <summary>Null for any file that is not a month file or its backup.</summary>
    private static string? MonthOfCopy(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var main = name.EndsWith(".json" + BackupSuffix, StringComparison.Ordinal) ? name[..^BackupSuffix.Length] : name;
        if (!main.StartsWith(MonthPrefix, StringComparison.Ordinal) || !main.EndsWith(".json", StringComparison.Ordinal))
        {
            return null;
        }

        var month = main[MonthPrefix.Length..^".json".Length];
        return IsMonthName(month) ? month : null;
    }

    private static DateTimeOffset? EffectiveDeletedBefore(HistoryState state) =>
        FromUnix(Max(state.DeletedBefore, state.RepairFloor));

    private static long CeilingSeconds(long milliseconds) =>
        milliseconds / 1000 + (milliseconds % 1000 > 0 ? 1 : 0);

    /// <param name="repaired">True when history files exist but the state had to be rebuilt.</param>
    /// <param name="storeMeta">
    /// The request store's meta when the caller holds the lock and the store
    /// could be opened: a repair then takes the deletion point and id from it.
    /// </param>
    private HistoryState ReadState(out bool existed, out bool repaired, ClaudeStoreMeta? storeMeta = null)
    {
        existed = false;
        repaired = false;
        var path = Path.Combine(Directory, StateFileName);

        // Both copies are read and merged so a deletion point never moves back:
        // the latest deletion and a finished cache recovery win.
        HistoryState? merged = null;
        foreach (var candidate in new[] { path, path + BackupSuffix })
        {
            var state = TryReadJson<HistoryState>(candidate, out _);
            if (!IsUsableState(state))
            {
                continue;
            }

            merged = merged is null
                ? state
                : merged with
                {
                    DeletedBefore = Max(merged.DeletedBefore, state!.DeletedBefore),
                    DeletionId = (state.DeletedBefore ?? long.MinValue) > (merged.DeletedBefore ?? long.MinValue)
                        ? state.DeletionId
                        : merged.DeletionId,
                    CacheRecovered = merged.CacheRecovered || state.CacheRecovered,
                    RecoveryAttempts = Math.Max(merged.RecoveryAttempts, state.RecoveryAttempts),
                    DeletionUnknown = merged.DeletionUnknown && state.DeletionUnknown,
                    RepairFloor = Max(merged.RepairFloor, state.RepairFloor),
                    ClaudeFreezeMs = (state.DeletedBefore ?? long.MinValue) > (merged.DeletedBefore ?? long.MinValue)
                        ? state.ClaudeFreezeMs
                        : (merged.DeletedBefore ?? long.MinValue) > (state.DeletedBefore ?? long.MinValue)
                            ? merged.ClaudeFreezeMs
                            : Max(merged.ClaudeFreezeMs, state.ClaudeFreezeMs)
                };
        }

        if (merged is not null)
        {
            existed = true;
            return merged;
        }

        if (!File.Exists(path) && !File.Exists(path + BackupSuffix) && ListMonths().Count == 0 && !_claude.FilesPresent)
        {
            // A brand-new history, so the cache recovery may still run.
            return new HistoryState { Schema = StateSchemaVersion };
        }

        // History files exist but the state is gone or damaged. The repair
        // never makes up a deletion id or point: it takes them from the
        // request store or from the month files' stamps, and otherwise says
        // they are unknown. The cache recovery never runs again.
        repaired = true;
        if (storeMeta is not null)
        {
            _logger.LogWarning("Usage history state was missing or damaged and has been repaired from the Claude request store");
            return new HistoryState
            {
                Schema = StateSchemaVersion,
                DeletedBefore = storeMeta.DeletedBeforeMs is { } ms && IsValidInstant(CeilingSeconds(ms)) ? CeilingSeconds(ms) : null,
                DeletionId = storeMeta.DeletionId,
                CacheRecovered = true,
                ClaudeFreezeMs = storeMeta.FreezeMs
            };
        }

        var oldest = ListMonths()
            .Select(month => ReadMonth(month, out _))
            .Select(file => file is null ? null : EarliestBucket(file))
            .Min();
        if (MonthStamp() is { } stamp)
        {
            // Months written by an older version carry the id but not the
            // point; usage older than the oldest saved bucket is then kept
            // out of view (never deleted) so a delete's logs cannot return.
            _logger.LogWarning("Usage history state was missing or damaged and has been repaired from the month files");
            return new HistoryState
            {
                Schema = StateSchemaVersion,
                DeletedBefore = stamp.DeletedBefore,
                DeletionId = stamp.DeletionId,
                CacheRecovered = true,
                RepairFloor = stamp.PointKnown ? null : oldest
            };
        }

        _logger.LogWarning("Usage history state was missing or damaged and its deletion point is unknown; older usage is kept out of view");
        return new HistoryState
        {
            Schema = StateSchemaVersion,
            CacheRecovered = true,
            DeletionUnknown = true,
            RepairFloor = oldest ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }

    /// <summary>Null when no readable month copy carries a known stamp.</summary>
    private (string? DeletionId, long? DeletedBefore, bool PointKnown)? MonthStamp()
    {
        (int Kind, long Point, DateTime Written, string? Id, long? DeletedBefore)? best = null;
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, MonthPrefix + "*"))
        {
            if (MonthOfCopy(Path.GetFileName(path)) is not { } month ||
                TryReadJson<MonthFile>(path, out _) is not { } file ||
                !IsValid(file, month) ||
                file.Stamp == MonthStampUnknown ||
                (file.DeletedBefore is { } point && !IsValidInstant(point)))
            {
                continue;
            }

            DateTime written;
            try
            {
                written = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var candidate = (file.Stamp == MonthStampKnown ? 1 : 0, file.DeletedBefore ?? long.MinValue, written, file.DeletionId, file.DeletedBefore);
            if (best is not { } current ||
                (candidate.Item1, candidate.Item2, candidate.written).CompareTo((current.Kind, current.Point, current.Written)) > 0)
            {
                best = candidate;
            }
        }

        return best is { } found ? (found.Id, found.DeletedBefore, found.Kind == 1) : null;
    }

    /// <summary>
    /// When the second write does not happen, the next locked open makes the
    /// two state copies equal before reading anything.
    /// </summary>
    private void WriteState(HistoryState state)
    {
        WriteJsonAtomically(Path.Combine(Directory, StateFileName), state);
        SyncStateBackup();
    }

    /// <summary>The caller holds the lock.</summary>
    /// <returns>True when the main state copy is valid.</returns>
    private bool SyncStateBackup()
    {
        var path = Path.Combine(Directory, StateFileName);
        var backup = path + BackupSuffix;
        if (!IsUsableState(TryReadJson<HistoryState>(path, out _)))
        {
            return false;
        }

        try
        {
            var main = File.ReadAllBytes(path);
            if (File.Exists(backup) && File.ReadAllBytes(backup).AsSpan().SequenceEqual(main))
            {
                return true;
            }

            var temp = backup + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(main);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, backup, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Usage history: the backup copy of the state could not be updated: {Error}", exception.GetType().Name);
        }

        return true;
    }

    private static bool IsUsableState(HistoryState? state) =>
        state is { Schema: StateSchemaVersion } &&
        (state.DeletedBefore is not { } point || IsValidInstant(point));

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

    private MonthFile? ReadMonth(string month, out bool damaged, HistoryState? state = null) =>
        ReadMonth(month, out damaged, out _, state);

    /// <summary>
    /// <paramref name="damaged"/> is true only when the main file exists and
    /// its content is invalid; a file that merely could not be opened (locked
    /// by a virus scanner, say) sets <paramref name="busy"/> instead, so a
    /// writer skips the month rather than rebuilding it from an older copy.
    /// </summary>
    /// <param name="state">
    /// A month copy not stamped with the latest delete's id was written before
    /// that delete, a leftover it could not remove, so none of its usage
    /// counts. One written by a state that did not know its deletion id is
    /// kept whole.
    /// </param>
    private MonthFile? ReadMonth(string month, out bool damaged, out bool busy, HistoryState? state = null)
    {
        var file = ReadMonthCopy(month, out damaged, out busy);
        return file is not null &&
            state is { DeletedBefore: not null } &&
            file.Stamp != MonthStampUnknown &&
            !string.Equals(file.DeletionId, state.DeletionId, StringComparison.Ordinal)
            ? file with { Rows = [] }
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
        HistoryState state)
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
            DeletionId = state.DeletionId,
            DeletedBefore = state.DeletedBefore,
            Stamp = state.DeletionUnknown ? MonthStampUnknown : MonthStampKnown,
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

    public static string MonthOf(long bucketStartUnixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(bucketStartUnixSeconds).UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static bool IsMonthName(string value) =>
        DateTime.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <returns>
    /// Null when another process holds the lock; callers skip their write and
    /// try again on the next cycle rather than write unlocked.
    /// </returns>
    private FileStream? TryLock() => TryLock(LockTimeout, quiet: false);

    /// <param name="quiet">True to leave the "busy" message to the caller.</param>
    private FileStream? TryLock(TimeSpan timeout, bool quiet)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, LockFileName);
        var deadline = DateTime.UtcNow + timeout;
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
                if (!quiet)
                {
                    _logger.LogWarning("Usage history is busy in another process; skipping this update");
                }

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

        /// <summary>Unix seconds.</summary>
        public long? DeletedBefore { get; init; }

        public bool CacheRecovered { get; init; }

        public int RecoveryAttempts { get; init; }

        /// <summary>
        /// Random rather than counted, so a repaired state can never reuse an
        /// id that an older month file still carries.
        /// </summary>
        public string? DeletionId { get; init; }

        /// <summary>
        /// True for a state rebuilt with no readable request store or month
        /// file to take its deletion id and point from. It never leads to
        /// removing anything.
        /// </summary>
        public bool DeletionUnknown { get; init; }

        /// <summary>
        /// Unix seconds before which usage is kept out of view after a repair
        /// that could not recover the deletion point. Only hides; never used
        /// to remove anything.
        /// </summary>
        public long? RepairFloor { get; init; }

        /// <summary>
        /// A copy of the request store's conversion instant, for when the store
        /// exists but cannot be opened. Cleared by a delete, which removes the store.
        /// </summary>
        public long? ClaudeFreezeMs { get; init; }
    }

    /// <summary>
    /// Rows are positional so a busy month stays small:
    /// <c>[bucketStartUnixSeconds, accountIndex, modelIndex, uncachedInput,
    /// cacheRead, cacheWrite5m, cacheWrite1h, output, reasoning, requests]</c>.
    /// </summary>
    private sealed record MonthFile
    {
        public int Schema { get; init; }
        public string Month { get; init; } = string.Empty;

        /// <summary>The state's deletion id when this file was written.</summary>
        public string? DeletionId { get; init; }

        /// <summary>The state's deletion point when this file was written, in Unix seconds.</summary>
        public long? DeletedBefore { get; init; }

        public int Stamp { get; init; }
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
