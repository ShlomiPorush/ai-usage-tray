using System.Globalization;
using System.Text.Json;
using costats.Application.Diagnostics;
using costats.Application.Storage;
using costats.Core.Analytics;
using Microsoft.Data.Sqlite;
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
/// <param name="MonthCount">Month files on disk.</param>
/// <param name="Bytes">Size of every history file.</param>
/// <param name="FirstBucket">The earliest saved usage, or null when none is shown.</param>
/// <param name="SavingPaused">
/// True while a delete of the history could not be finished yet: nothing is
/// saved for any provider until it is.
/// </param>
public sealed record UsageHistoryInfo(int MonthCount, long Bytes, DateTimeOffset? FirstBucket, bool SavingPaused = false)
{
    public static readonly UsageHistoryInfo Empty = new(0, 0, null);

    /// <summary>Saving is paused and nothing about the stored history can be told.</summary>
    public static readonly UsageHistoryInfo Paused = new(0, 0, null, SavingPaused: true);
}

/// <summary>
/// What a sync read under the history lock before its scan: the deletion
/// point it scans against. A sync whose point changed by the time it writes
/// writes nothing.
/// </summary>
/// <param name="DeletionId">The state's deletion id, after any repair was saved.</param>
/// <param name="DeletedBefore">The later of the state's and the request store's deletion points.</param>
internal sealed record HistorySyncStart(string? DeletionId, DateTimeOffset? DeletedBefore);

/// <summary>The one-time conversion a sync asks for: the instant it froze at and the marks.</summary>
internal sealed record ClaudeConversion(long FreezeMs, IReadOnlyList<ClaudeRequestRow> Marks);

/// <summary>Everything one sync writes, computed from what the store holds.</summary>
/// <param name="Buckets">Buckets for the month files, merged larger-wins.</param>
/// <param name="Names">Current names of the accounts in <paramref name="Buckets"/>.</param>
/// <param name="CacheRecovered">Marks the one-time cache recovery as done.</param>
/// <param name="Records">New Claude records.</param>
/// <param name="StoreNames">Current names of the accounts the request store holds rows for.</param>
/// <param name="Conversion">The conversion, when this sync may perform it.</param>
internal sealed record HistoryWritePlan(
    IReadOnlyDictionary<UsageHistoryKey, UsageHistoryBucket> Buckets,
    IReadOnlyCollection<UsageHistoryAccount> Names,
    bool CacheRecovered,
    IReadOnlyList<ClaudeRequestRow> Records,
    IReadOnlyCollection<UsageHistoryAccount> StoreNames,
    ClaudeConversion? Conversion);

/// <summary>What a sync's write did.</summary>
/// <param name="Changed">Buckets added or replaced.</param>
/// <param name="RecordsAdded">Claude records added.</param>
/// <param name="Converted">True when the conversion was committed.</param>
/// <param name="Skipped">True when a delete happened since the sync started, so nothing was written.</param>
/// <param name="NewestRecordMs">The newest record's timestamp, for the clock check before a prune.</param>
internal sealed record HistorySaveOutcome(int Changed, int RecordsAdded, bool Converted, bool Skipped, long? NewestRecordMs);

/// <summary>Everything a report reads from the history, from one hold of the lock.</summary>
/// <param name="Snapshot">
/// Month files and state. <see cref="UsageHistorySnapshot.DeletedBefore"/> is
/// the later of the state's and the request store's deletion points, and
/// <see cref="UsageHistorySnapshot.Accounts"/> includes the store's names.
/// </param>
/// <param name="Claude">The request store.</param>
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
/// connection. After each change it is copied to <c>.bak</c> through a checked
/// temporary file; a damaged store is replaced by that copy, trimmed again to
/// the latest delete and prune points so a stale copy brings nothing back.
/// </para>
/// <para>
/// Deleting the history is recorded first in <c>deletion-journal.json</c> (two
/// copies), then carried out: the request store, every month file and both
/// state copies take the new deletion id and point, and the journal is
/// removed last. Every locked open finishes a journal it finds before it
/// reads or writes anything else; until it can be finished, nothing is saved
/// and reports show only the logs after the deletion point. A lost state is
/// rebuilt from the request store or the month files' stamps, never from a
/// guess, and an unknown deletion id never leads to removing anything.
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
    private const string JournalFileName = "deletion-journal.json";
    private const int JournalSchemaVersion = 1;

    // How a month file was stamped: by an older version (deletion id only),
    // by a state that knew its deletion id and point, or by a state that
    // did not know them.
    private const int MonthStampOldVersion = 0;
    private const int MonthStampKnown = 1;
    private const int MonthStampUnknown = 2;
    private const string BackupSuffix = ".bak";

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long Settings waits for the lock before it shows the month files alone.</summary>
    private static readonly TimeSpan InspectLockTimeout = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly ILogger _logger;
    private readonly ClaudeRequestStore _claude;

    /// <summary>
    /// Creates a store rooted at <paramref name="directory"/>, or at the default
    /// <c>%LOCALAPPDATA%\AIUsageTray\history</c> when null.
    /// </summary>
    public UsageHistoryStore(string? directory = null, ILogger? logger = null)
    {
        Directory = directory ?? DefaultDirectory();
        _logger = logger ?? NullLogger.Instance;
        _claude = new ClaudeRequestStore(Directory, _logger);
    }

    /// <summary>Where the history files live.</summary>
    public string Directory { get; }

    /// <summary>How long a report waits for the lock before it reports without the request store.</summary>
    internal TimeSpan ReportLockTimeout { get; init; } = LockTimeout;

    /// <summary>
    /// Test hook, called with the name of a step at the points where a stopped
    /// app would leave the files half written. Null outside tests.
    /// </summary>
    internal Action<string>? StopAt { get; init; }

    /// <summary>
    /// Whether a deletion journal copy exists: true, false, or null when that
    /// cannot be determined (access denied, say). Replaceable in tests.
    /// </summary>
    internal Func<string, bool?> JournalProbe { get; init; } = ProbeFile;

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

        using var claude = OpenLocked(out var state, out _, out _, out var pending);
        if (pending is not null)
        {
            return null;
        }

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

        var state = ReadState(out _, out _);
        return ReadJournal(persist: false) is { } journal
            ? LogsOnly(state, journal)
            : ReadSnapshot(state, EffectiveDeletedBefore(state));
    }

    /// <summary>
    /// What a delete that is not finished yet leaves visible: no saved usage
    /// at all, and nothing from the logs before its point.
    /// </summary>
    private static UsageHistorySnapshot LogsOnly(HistoryState state, DeletionJournal journal) =>
        new(
            new Dictionary<UsageHistoryKey, UsageHistoryBucket>(),
            new Dictionary<string, UsageHistoryAccount>(StringComparer.Ordinal),
            FromUnix(Math.Max(EffectiveDeletedBefore(state)?.ToUnixTimeSeconds() ?? long.MinValue, journal.DeletedBefore)));

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

        using var claude = OpenLocked(out var state, out var stateExisted, out _, out var pending);
        if (pending is not null)
        {
            return null;
        }

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
    /// Starts a sync: takes the lock, saves a repaired state so its deletion id
    /// stays the same from now on, and returns the deletion point the sync
    /// scans against.
    /// </summary>
    /// <returns>
    /// Null when the lock was busy, or a delete could not be finished yet; the
    /// sync then skips this round and saves nothing for any provider.
    /// </returns>
    internal HistorySyncStart? BeginSync()
    {
        using var gate = TryLock();
        if (gate is null)
        {
            return null;
        }

        using var claude = OpenLocked(out var state, out _, out var repaired, out var pending);
        if (pending is not null)
        {
            return null;
        }

        if (repaired)
        {
            WriteState(state);
        }

        return new HistorySyncStart(state.DeletionId, EffectiveDeletedBefore(state));
    }

    /// <summary>
    /// Writes one sync under a single hold of the lock: reads the request
    /// store, lets <paramref name="plan"/> decide what to write from it, then
    /// merges the month files, adds the Claude records, and performs the
    /// one-time conversion when the plan asks for it and every month with
    /// Claude usage was written.
    /// </summary>
    /// <param name="start">What the sync read before its scan. A delete since then makes this write nothing.</param>
    /// <param name="plan">Computes the writes from the store's content.</param>
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

        var claude = OpenLocked(out var state, out var stateExisted, out _, out var pending);
        try
        {
            if (pending is not null)
            {
                return null;
            }

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
                var meta = new ClaudeStoreMeta(
                    conversion.FreezeMs,
                    state.DeletionId,
                    state.DeletedBefore * 1000,
                    PruneFloors: FloorsOf(state));
                if (_claude.Create(meta, [.. conversion.Marks], write.StoreNames))
                {
                    converted = true;
                    state = state with { ClaudeFreezeMs = conversion.FreezeMs };
                    WriteState(state);
                    claude.Dispose();
                    claude = OpenClaude();
                    claude.Backup();
                    _logger.LogInformation(
                        "Usage history: Claude requests are now kept by identity ({Marks} requests marked as already saved)",
                        conversion.Marks.Count);
                }
            }

            if (!converted)
            {
                claude.Backup();
            }

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
    /// Reads the state, every month and the request store under one hold of
    /// the lock, for a report. When the lock is busy, the month files are read
    /// without it and the store is left out.
    /// </summary>
    internal HistoryReportRead ReadForReport()
    {
        using var gate = TryLock(ReportLockTimeout, quiet: true);
        if (gate is null)
        {
            _logger.LogDebug("Usage history is busy; reporting without the saved Claude requests");
            var unlocked = ReadState(out _, out _);
            if (ReadJournal(persist: false) is { } unfinished)
            {
                return new HistoryReportRead(LogsOnly(unlocked, unfinished), ClaudeRequestView.Absent);
            }

            return new HistoryReportRead(
                ReadSnapshot(unlocked, EffectiveDeletedBefore(unlocked)),
                _claude.FilesPresent ? ClaudeRequestView.Unavailable(unlocked.ClaudeFreezeMs) : ClaudeRequestView.Absent);
        }

        using var claude = OpenLocked(out var state, out _, out _, out var pending);
        if (pending is not null)
        {
            // A delete that could not be finished yet: the logs alone, after its point.
            return new HistoryReportRead(LogsOnly(state, pending), ClaudeRequestView.Absent);
        }

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

        // The cache recovery counts as done only once everything it found is
        // saved (callers check AllWritten); otherwise it runs again on the next sync.
        return new MergeResult(changed, allWritten, busyMonths);
    }

    /// <summary>
    /// Drops every bucket that starts before <paramref name="cutoff"/>: whole
    /// months are deleted, the month the cutoff falls in is rewritten.
    /// </summary>
    /// <remarks>
    /// Claude requests kept by identity go too: records logged before the
    /// cutoff, and marks whose bucket starts before it, but nothing from the
    /// earliest month that could not be pruned onwards, so a mark never goes
    /// while its bucket stays. That floor is saved in
    /// the state before any row goes, and in the request store with the
    /// delete, so a backup restored later is always trimmed to it.
    /// </remarks>
    /// <returns>How many buckets and requests were removed, or null when the lock was busy.</returns>
    public int? Prune(DateTimeOffset cutoff)
    {
        using var gate = TryLock();
        if (gate is null)
        {
            return null;
        }

        using var claude = OpenLocked(out var state, out _, out _, out var pending);
        if (pending is not null)
        {
            return null;
        }

        var cutoffSeconds = cutoff.ToUnixTimeSeconds();
        var removedMonths = PruneMonths(state, cutoff, out var keptFrom);
        var removedRequests = 0;
        if (claude.Status == ClaudeStoreStatus.Available)
        {
            try
            {
                // Rows go only below what the month prune really removed: a
                // mark whose bucket is still in a month that could not be
                // pruned (busy, say) must stay, or a copy of its request
                // would be counted again next to that bucket.
                var floorSeconds = keptFrom is { } kept ? Math.Min(cutoffSeconds, kept) : cutoffSeconds;
                var floorMs = floorSeconds * 1000;
                if (claude.CountBelow(floorMs) > 0)
                {
                    // The prune is saved in the state before any row goes, so
                    // a backup restored after a stop is always trimmed to it.
                    var floor = claude.PruneFloorAt(floorSeconds);
                    state = state with { PruneFloors = FloorsToState(ClaudePruneFloor.Merge(FloorsOf(state), [floor])) };
                    WriteState(state);

                    removedRequests = claude.Prune(floor);
                    Step("prune-rows-removed");
                }
            }
            catch (Exception exception) when (ClaudeRequestStore.IsRecoverable(exception))
            {
                _logger.LogWarning("Usage history: expired Claude requests could not be removed: {Error}", exception.GetType().Name);
            }

            claude.Backup();
        }

        return removedMonths + removedRequests;
    }

    /// <param name="state">The current state.</param>
    /// <param name="cutoff">Buckets starting before it go.</param>
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
    /// Deletes all stored history and records that usage before
    /// <paramref name="now"/> must not be shown again, so logs still on disk
    /// cannot bring it back. The deletion point only ever moves forward.
    /// </summary>
    /// <remarks>
    /// The delete is recorded first in a deletion journal (two copies, each
    /// written through a flushed temporary file) and only then carried out:
    /// the request store, every month file and both state copies. The
    /// journal is removed last. Stopped at any point, the next locked open
    /// finishes it before it reads or writes anything else.
    /// </remarks>
    /// <returns>
    /// False when the lock was busy and nothing was deleted, or when some
    /// files could not be removed yet. The delete then stays recorded: no
    /// saved usage is shown and nothing is saved until it is finished.
    /// </returns>
    public bool DeleteAll(DateTimeOffset now)
    {
        using var gate = TryLock();
        if (gate is null)
        {
            return false;
        }

        HistoryState state;
        DeletionJournal? pending;
        using (OpenLocked(out state, out _, out _, out pending, restore: false))
        {
            // Opened only to finish an earlier delete and read the state; the
            // connection is closed before any store file is removed.
        }

        // One second past now so a request logged in this very second is
        // gone too. The deletion point only ever moves forward.
        var point = Math.Max(
            Math.Max(
                EffectiveDeletedBefore(state)?.ToUnixTimeSeconds() ?? long.MinValue,
                pending is { Undetermined: false } known ? known.DeletedBefore : long.MinValue),
            now.ToUnixTimeSeconds() + 1);
        try
        {
            WriteJournal(new DeletionJournal
            {
                Schema = JournalSchemaVersion,
                DeletionId = Guid.NewGuid().ToString("n"),
                DeletedBefore = point
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing was removed; a copy written before the failure is
            // finished like any other journal.
            _logger.LogWarning("Usage history: the delete could not be recorded: {Error}", exception.GetType().Name);
            return false;
        }

        Step("journal-written");
        return CompleteJournal(out _);
    }

    private static long MonthStart(string month) =>
        new DateTimeOffset(
            DateTime.ParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            TimeSpan.Zero).ToUnixTimeSeconds();

    /// <summary>Size and span of the stored history.</summary>
    public UsageHistoryInfo Inspect()
    {
        // The journal first: a pending delete, or one whose presence cannot
        // be checked, pauses saving whatever else can be read.
        var journal = ReadJournal(persist: false);
        if (journal is { Undetermined: true })
        {
            return UsageHistoryInfo.Paused;
        }

        switch (ProbeFile(Directory))
        {
            case false:
                return UsageHistoryInfo.Empty;
            case null:
                return UsageHistoryInfo.Paused;
        }

        try
        {
            return InspectFiles(paused: journal is not null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The folder cannot be listed, so neither can it be told whether
            // a delete is pending: the paused state shows, not "no history".
            _logger.LogWarning("Usage history could not be inspected: {Error}", exception.GetType().Name);
            return UsageHistoryInfo.Paused;
        }
    }

    private UsageHistoryInfo InspectFiles(bool paused)
    {
        Step("inspect-enumerate");
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

        // The request store is opened only under the lock, and Settings does
        // not wait long for it: when busy, the month files alone are shown.
        long? earliestRecordMs = null;
        ClaudeStoreMeta? meta = null;
        if (_claude.FilesPresent)
        {
            using var gate = TryLock(InspectLockTimeout, quiet: true);
            if (gate is not null)
            {
                using var claude = _claude.Open(_ => default, restore: false);
                meta = claude.Meta;
                try
                {
                    earliestRecordMs = claude.EarliestRecordMs();
                }
                catch (Exception exception) when (ClaudeRequestStore.IsRecoverable(exception))
                {
                    earliestRecordMs = null;
                }
            }
        }

        var state = ReadState(out _, out _, meta);
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

        // A delete that is not finished yet hides all saved usage and pauses saving.
        return new UsageHistoryInfo(months.Count, bytes, paused ? null : first, paused);
    }

    // -- State --------------------------------------------------------------

    /// <summary>
    /// Opens the request store and reads the state; the caller holds the lock.
    /// A deletion journal is finished first, before anything else is read or
    /// written. When it cannot be finished yet (a store or month file is
    /// locked, say), <paramref name="pending"/> is that journal: the caller
    /// then writes nothing at all, and reports show the logs alone after its
    /// point. A state rebuilt without knowing its deletion id takes it from
    /// the request store once that can be read; nothing is ever removed on
    /// account of an unknown id.
    /// </summary>
    private ClaudeStoreSession OpenLocked(
        out HistoryState state,
        out bool existed,
        out bool repaired,
        out DeletionJournal? pending,
        bool restore = true)
    {
        if (!CompleteJournal(out pending))
        {
            state = ReadState(out existed, out repaired);
            return ClaudeStoreSession.Unavailable;
        }

        SyncStateBackup();
        var claude = OpenClaude(restore);
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
                PruneFloors = FloorsToState(ClaudePruneFloor.Merge(FloorsOf(state), meta.Floors)),
                ClaudeFreezeMs = meta.FreezeMs
            };
            WriteState(state);
            RestampUnknownMonths(state);
            existed = true;
            repaired = false;
        }

        // A write sequence is never reused once a prune record has seen it.
        if (claude.Status == ClaudeStoreStatus.Available &&
            ClaudePruneFloor.HighestSeq(FloorsOf(state)) > claude.Meta!.WriteSeq)
        {
            try
            {
                claude.AdvanceWriteSeq(ClaudePruneFloor.HighestSeq(FloorsOf(state)));
            }
            catch (Exception exception) when (ClaudeRequestStore.IsRecoverable(exception))
            {
                _logger.LogWarning("Usage history: the Claude request store could not be updated: {Error}", exception.GetType().Name);
                claude.Dispose();
                return ClaudeStoreSession.Unavailable;
            }
        }

        return claude;
    }

    /// <summary>
    /// Rewrites every month written while the deletion id was unknown with the
    /// id and point just adopted, keeping all of its usage. A month that
    /// cannot be rewritten now keeps its unknown stamp and stays whole.
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

    /// <summary>
    /// The point of a delete that is not finished yet, read without the lock
    /// and without writing anything, or null when there is none. The last
    /// instant there is when it cannot be told whether a delete is pending.
    /// </summary>
    public DateTimeOffset? PendingDeletionPoint() =>
        ReadJournal(persist: false) is { } journal ? FromUnix(journal.DeletedBefore) : null;

    // -- Deletion journal ---------------------------------------------------

    private string JournalPath => Path.Combine(Directory, JournalFileName);

    /// <summary>
    /// Finishes the delete the journal records: the request store goes unless
    /// it was created after this delete, every month copy not written after
    /// it goes, both state copies take its id and point, and only then is the
    /// journal removed. Each step may run again after a stop: a store or a
    /// month stamped with the journal's id is kept, and a state that already
    /// holds the id is not rewritten.
    /// </summary>
    /// <param name="journal">The delete still to finish, or null when none is left.</param>
    /// <returns>True when no journal is left.</returns>
    /// <remarks>
    /// Any failure on the way (a locked or read-only file, say) leaves the
    /// journal in place and the delete pending; it never escapes to the caller.
    /// </remarks>
    private bool CompleteJournal(out DeletionJournal? journal)
    {
        journal = null;
        try
        {
            return CompleteJournalSteps(out journal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            _logger.LogWarning(
                "Usage history: a delete could not be finished yet: {ErrorKind}: {Error}",
                exception.GetType().Name,
                LogRedaction.Redact(exception.Message));
            journal ??= ReadJournal(persist: false);
            return journal is null;
        }
    }

    private bool CompleteJournalSteps(out DeletionJournal? journal)
    {
        journal = ReadJournal(persist: true);
        if (journal is null)
        {
            return true;
        }

        if (journal.Undetermined)
        {
            _logger.LogWarning("Usage history: whether a delete is pending cannot be checked; nothing is saved meanwhile");
            return false;
        }

        // Every store file counts, leftovers and set-aside copies included;
        // all of them stay only when the store was created after this delete.
        if (_claude.AnyFiles && !_claude.CarriesDeletionId(journal.DeletionId) && _claude.DeleteAllFiles() > 0)
        {
            _logger.LogWarning("Usage history: a delete is waiting for the Claude request store; nothing is saved until it is finished");
            return false;
        }

        Step("store-removed");
        if (!RemoveMonthsNotStampedWith(journal.DeletionId))
        {
            _logger.LogWarning("Usage history: a delete is waiting for a month file; nothing is saved until it is finished");
            return false;
        }

        Step("months-removed");
        var state = ReadState(out var existed, out _);
        if (!existed || state.DeletionUnknown || !string.Equals(state.DeletionId, journal.DeletionId, StringComparison.Ordinal))
        {
            WriteState(state with
            {
                DeletedBefore = Math.Max(state.DeletedBefore ?? long.MinValue, journal.DeletedBefore),
                DeletionId = journal.DeletionId,
                DeletionUnknown = false,
                RepairFloor = null,
                CacheRecovered = true,
                ClaudeFreezeMs = null,

                // Every row is gone, so no earlier prune can claim anything.
                PruneFloors = null
            });
        }
        else if (!StateCopiesMatch())
        {
            // Already this delete's state, but a copy is missing or damaged.
            WriteState(state);
        }

        if (!StateCopiesMatch())
        {
            _logger.LogWarning("Usage history: a delete is waiting for the state files; nothing is saved until it is finished");
            return false;
        }

        Step("state-written");
        if (!RemoveJournal())
        {
            return false;
        }

        _logger.LogInformation("Usage history delete finished");
        journal = null;
        return true;
    }

    /// <summary>
    /// The deletion journal, or null when there is none. When copies exist
    /// but none can be read, a delete was still intended: it is rebuilt with
    /// a new id and the newer copy's write time as its point (saved again
    /// when <paramref name="persist"/>, which needs the lock).
    /// </summary>
    /// <remarks>
    /// When it cannot even be determined whether a copy exists, a delete may
    /// be pending with any point, even one later than the clock says now (a
    /// clock set back). The result is then an undetermined journal whose
    /// point is the last instant there is: nothing at all shows and nothing
    /// is saved until the journal can be checked again.
    /// </remarks>
    private DeletionJournal? ReadJournal(bool persist)
    {
        string[] copies = [JournalPath, JournalPath + BackupSuffix];
        var found = copies.Select(copy => JournalProbe(copy)).ToList();
        if (found.Any(exists => exists is null))
        {
            return new DeletionJournal
            {
                Schema = JournalSchemaVersion,
                DeletedBefore = MaxUnixSeconds,
                Undetermined = true
            };
        }

        var present = copies.Where((_, index) => found[index] == true).ToList();
        if (present.Count == 0)
        {
            return null;
        }

        var readable = copies
            .Select(copy => TryReadJson<DeletionJournal>(copy, out _))
            .Where(journal => journal is { Schema: JournalSchemaVersion, DeletionId.Length: > 0 } && IsValidInstant(journal.DeletedBefore))
            .ToList();
        if (readable.Count > 0)
        {
            return readable.MaxBy(journal => journal!.DeletedBefore);
        }

        var written = present
            .Select(copy =>
            {
                try
                {
                    return File.GetLastWriteTimeUtc(copy);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return DateTime.UtcNow;
                }
            })
            .Max();
        var rebuilt = new DeletionJournal
        {
            Schema = JournalSchemaVersion,
            DeletionId = Guid.NewGuid().ToString("n"),
            DeletedBefore = CeilingSeconds(new DateTimeOffset(DateTime.SpecifyKind(written, DateTimeKind.Utc)).ToUnixTimeMilliseconds())
        };
        if (persist)
        {
            _logger.LogWarning("Usage history: the deletion journal was damaged; the delete is finished from its write time");
            try
            {
                WriteJournal(rebuilt);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Usage history: the deletion journal could not be rewritten: {Error}", exception.GetType().Name);
            }
        }

        return rebuilt;
    }

    private void WriteJournal(DeletionJournal journal)
    {
        WriteFileAtomically(JournalPath, journal);
        Step("journal-main-written");
        WriteFileAtomically(JournalPath + BackupSuffix, journal);
    }

    /// <returns>True when both copies are gone.</returns>
    private bool RemoveJournal()
    {
        TryDelete(JournalPath);
        Step("journal-main-removed");
        TryDelete(JournalPath + BackupSuffix);
        TryDelete(JournalPath + ".tmp");
        TryDelete(JournalPath + BackupSuffix + ".tmp");
        return JournalProbe(JournalPath) == false && JournalProbe(JournalPath + BackupSuffix) == false;
    }

    /// <summary>True when the file exists, false when it does not, null when that cannot be told.</summary>
    private static bool? ProbeFile(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes every month file (main, backup, temporary and set-aside
    /// copies) that is not a readable month stamped with
    /// <paramref name="deletionId"/>, the ones written after that delete.
    /// </summary>
    /// <returns>True when nothing else is left.</returns>
    private bool RemoveMonthsNotStampedWith(string deletionId)
    {
        var allGone = true;
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, MonthPrefix + "*").ToList())
        {
            if (MonthOfCopy(Path.GetFileName(path)) is { } month &&
                TryReadJson<MonthFile>(path, out _) is var file &&
                IsValid(file, month) &&
                string.Equals(file!.DeletionId, deletionId, StringComparison.Ordinal))
            {
                continue;
            }

            TryDelete(path);
            allGone &= !File.Exists(path);
        }

        return allGone;
    }

    /// <summary>The month a main or backup month file holds, or null for any other file.</summary>
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

    private bool StateCopiesMatch()
    {
        var path = Path.Combine(Directory, StateFileName);
        try
        {
            return IsUsableState(TryReadJson<HistoryState>(path, out _)) &&
                File.Exists(path + BackupSuffix) &&
                File.ReadAllBytes(path).AsSpan().SequenceEqual(File.ReadAllBytes(path + BackupSuffix));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void WriteFileAtomically<T>(string path, T value)
    {
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, SerializerOptions);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    private static IReadOnlyList<ClaudePruneFloor> FloorsOf(HistoryState state) =>
        ClaudePruneFloor.Merge(
            (state.PruneFloors ?? []).Where(entry => entry is { Length: 2 }).Select(entry => new ClaudePruneFloor(entry[0], entry[1])),
            []);

    private static long[][] FloorsToState(IEnumerable<ClaudePruneFloor> floors) =>
        [.. floors.Select(floor => new[] { floor.Seq, floor.FloorSeconds })];

    /// <summary>Runs the test hook for a named step; a test throws from it to simulate the app stopping there.</summary>
    private void Step(string name) => StopAt?.Invoke(name);

    /// <summary>Opens the request store; the caller holds the lock.</summary>
    private ClaudeStoreSession OpenClaude(bool restore = true) =>
        _claude.Open(backupMeta => BarrierOf(ReadState(out _, out _, backupMeta)), restore);

    /// <summary>
    /// What a restore may apply from the state. A state that does not know its
    /// deletion id applies neither the id nor a point.
    /// </summary>
    private static ClaudeStoreBarrier BarrierOf(HistoryState state) =>
        state.DeletionUnknown
            ? new(null, null, false, FloorsOf(state))
            : new(state.DeletionId, state.DeletedBefore * 1000, true, FloorsOf(state));

    /// <summary>
    /// The instant before which usage is hidden: the deletion point, or the
    /// floor a repair could not do without, whichever is later.
    /// </summary>
    private static DateTimeOffset? EffectiveDeletedBefore(HistoryState state) =>
        FromUnix(Max(state.DeletedBefore, state.RepairFloor));

    private static long CeilingSeconds(long milliseconds) =>
        milliseconds / 1000 + (milliseconds % 1000 > 0 ? 1 : 0);

    private HistoryState ReadState(out bool existed) => ReadState(out existed, out _);

    /// <param name="existed">True when a state file was read.</param>
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
                    PruneFloors = FloorsToState(ClaudePruneFloor.Merge(FloorsOf(merged), FloorsOf(state))),
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
            // A brand-new history: nothing deleted yet, cache recovery pending.
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
                PruneFloors = FloorsToState(storeMeta.Floors),
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

    /// <summary>
    /// The deletion id and point the newest readable month copies were written
    /// under, or null when no month copy carries a known stamp.
    /// </summary>
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
    /// Writes the state, then makes the backup copy equal to it, so the two
    /// copies only differ if the app stops in between; the next locked open
    /// repairs that before reading anything.
    /// </summary>
    private void WriteState(HistoryState state)
    {
        WriteJsonAtomically(Path.Combine(Directory, StateFileName), state);
        Step("state-main-written");
        SyncStateBackup();
    }

    /// <summary>
    /// When <c>state.json</c> is valid, rewrites <c>state.json.bak</c> to the
    /// same bytes if it differs. The caller holds the lock.
    /// </summary>
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

        // Only a month stamped with another known deletion id was written
        // before the current delete. One written by a state that did not know
        // its deletion id, or with no id at all, is kept whole.
        return file is not null &&
            state is { DeletedBefore: { } point } &&
            file.Stamp != MonthStampUnknown &&
            !string.IsNullOrEmpty(file.DeletionId) &&
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
    private FileStream? TryLock() => TryLock(LockTimeout, quiet: false);

    /// <param name="timeout">How long to wait for another writer.</param>
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

        /// <summary>
        /// The prunes committed in the Claude request store, as
        /// <c>[writeSeq, floorUnixSeconds]</c> pairs; a backup restored later
        /// is trimmed to exactly what they removed.
        /// </summary>
        public long[][]? PruneFloors { get; init; }

        /// <summary>
        /// True for a state rebuilt with nothing to take its deletion id and
        /// point from (no readable request store or month file). It never
        /// leads to removing anything; the id and point are taken from the
        /// request store as soon as it can be read.
        /// </summary>
        public bool DeletionUnknown { get; init; }

        /// <summary>
        /// Unix seconds before which usage is kept out of view after a repair
        /// that could not recover the deletion point. Only hides; never used
        /// to remove anything.
        /// </summary>
        public long? RepairFloor { get; init; }

        /// <summary>
        /// A copy of the request store's conversion instant, in Unix
        /// milliseconds, for when the store exists but cannot be opened.
        /// Cleared by a delete, which removes the store.
        /// </summary>
        public long? ClaudeFreezeMs { get; init; }
    }

    /// <summary>
    /// A delete in progress: written before anything is removed, removed once
    /// the request store, every month file and both state copies reflect it.
    /// </summary>
    private sealed record DeletionJournal
    {
        public int Schema { get; init; }

        /// <summary>The new deletion id; only a delete makes one.</summary>
        public string DeletionId { get; init; } = string.Empty;

        /// <summary>Unix seconds; usage before this is deleted.</summary>
        public long DeletedBefore { get; init; }

        /// <summary>
        /// Not read from a file: whether a journal exists could not be
        /// determined, so a delete may be pending.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool Undetermined { get; init; }
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

        /// <summary>The state's deletion point when this file was written, in Unix seconds.</summary>
        public long? DeletedBefore { get; init; }

        /// <summary>
        /// How this file was stamped: 0 by an older version (id only), 1 with a
        /// known id and point, 2 by a state that did not know them.
        /// </summary>
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
