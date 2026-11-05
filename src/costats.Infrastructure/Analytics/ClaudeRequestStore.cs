using System.Globalization;
using costats.Core.Analytics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace costats.Infrastructure.Analytics;

/// <summary>
/// One Claude request the usage history knows by its identity.
/// </summary>
/// <param name="Key">The request identity, <see cref="UsageSample.DedupKey"/>; never 0.</param>
/// <param name="IsMark">
/// True for a mark: a request that was already counted in a month-file bucket
/// when the store was created, and stays counted there. False for a record:
/// a request first seen after that, counted from this row alone.
/// </param>
/// <param name="TimestampMs">The request's log timestamp, in Unix milliseconds.</param>
/// <param name="BucketStart">Unix seconds of the 15-minute bucket the request belongs to.</param>
/// <param name="Account">The history account key it is counted under.</param>
/// <param name="Model">The model id, as a bucket stores it.</param>
/// <param name="Tokens">The token counts it is counted with.</param>
internal sealed record ClaudeRequestRow(
    long Key,
    bool IsMark,
    long TimestampMs,
    long BucketStart,
    string Account,
    string Model,
    UsageTokens Tokens)
{
    /// <summary>The request's log timestamp.</summary>
    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs);

    /// <summary>The row for a sample counted under <paramref name="account"/>.</summary>
    public static ClaudeRequestRow From(UsageSample sample, string account, bool isMark) => new(
        sample.DedupKey,
        isMark,
        sample.Timestamp.ToUnixTimeMilliseconds(),
        UsageHistory.BucketStartOf(sample.Timestamp),
        account,
        UsageHistory.NormalizeModel(sample.Model),
        sample.Tokens);

    /// <summary>
    /// The row as a sample under <paramref name="accountId"/>: one request at
    /// its exact timestamp, with the values it is counted with.
    /// </summary>
    public UsageSample ToSample(string accountId) =>
        new(Timestamp, UsageProviderKind.Claude, accountId, Model, Tokens) { DedupKey = Key };
}

/// <summary>What the request store's <c>meta</c> table says.</summary>
/// <param name="FreezeMs">
/// Unix milliseconds of the conversion: requests logged at or after it are
/// saved as records, requests before it follow the month-file rules.
/// </param>
/// <param name="DeletionId">
/// The history's deletion id when the store was created. A delete removes
/// the store, so a store only ever carries the id of the latest delete
/// before it was created.
/// </param>
/// <param name="DeletedBeforeMs">That delete's point, in Unix milliseconds.</param>
/// <param name="WriteSeq">
/// Sequence of the last write transaction; every row carries the sequence
/// it was saved under.
/// </param>
/// <param name="PruneFloors">The prunes committed so far, see <see cref="ClaudePruneFloor"/>.</param>
internal sealed record ClaudeStoreMeta(
    long FreezeMs,
    string? DeletionId,
    long? DeletedBeforeMs,
    long WriteSeq = 0,
    IReadOnlyList<ClaudePruneFloor>? PruneFloors = null)
{
    /// <summary>The prunes committed so far; never null.</summary>
    public IReadOnlyList<ClaudePruneFloor> Floors => PruneFloors ?? [];
}

/// <summary>
/// One committed prune: it removed every row saved up to write sequence
/// <paramref name="Seq"/> that lies below <paramref name="FloorSeconds"/>
/// (records by timestamp, marks by bucket start). A row saved later, for
/// example after the retention was extended, was not removed by it, so a
/// restore never removes it either.
/// </summary>
internal readonly record struct ClaudePruneFloor(long Seq, long FloorSeconds)
{
    /// <summary>
    /// The smallest list that removes the same rows: an entry is dropped when
    /// a later prune (higher sequence) has a floor at least as high.
    /// </summary>
    public static IReadOnlyList<ClaudePruneFloor> Merge(IEnumerable<ClaudePruneFloor> left, IEnumerable<ClaudePruneFloor> right)
    {
        var kept = new List<ClaudePruneFloor>();
        long? highest = null;
        foreach (var floor in left.Concat(right).OrderByDescending(entry => entry.Seq).ThenByDescending(entry => entry.FloorSeconds))
        {
            if (highest is null || floor.FloorSeconds > highest)
            {
                kept.Add(floor);
                highest = floor.FloorSeconds;
            }
        }

        kept.Reverse();
        return kept;
    }

    /// <summary>The highest write sequence any of <paramref name="floors"/> covers, or 0.</summary>
    public static long HighestSeq(IEnumerable<ClaudePruneFloor> floors) =>
        floors.Select(floor => floor.Seq).DefaultIfEmpty(0).Max();

    /// <summary>"seq:floor;seq:floor", the meta table's form.</summary>
    public static string Format(IEnumerable<ClaudePruneFloor> floors) =>
        string.Join(';', floors.Select(floor =>
            floor.Seq.ToString(CultureInfo.InvariantCulture) + ":" + floor.FloorSeconds.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Reads <see cref="Format"/>; malformed entries are skipped.</summary>
    public static IReadOnlyList<ClaudePruneFloor> Parse(string? text)
    {
        var floors = new List<ClaudePruneFloor>();
        foreach (var part in (text ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split(':');
            if (pieces.Length == 2 &&
                long.TryParse(pieces[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq) &&
                long.TryParse(pieces[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var floor))
            {
                floors.Add(new ClaudePruneFloor(seq, floor));
            }
        }

        return Merge(floors, []);
    }
}

/// <summary>
/// What the history state knows about deletes and prunes, for a restore.
/// </summary>
/// <param name="DeletionId">The state's deletion id.</param>
/// <param name="DeletedBeforeMs">The state's deletion point.</param>
/// <param name="DeletionKnown">
/// False when the state could not be rebuilt from anything and its deletion
/// id is unknown; then neither the id nor the point is applied.
/// </param>
/// <param name="PruneFloors">The prunes the state knows.</param>
internal readonly record struct ClaudeStoreBarrier(
    string? DeletionId,
    long? DeletedBeforeMs,
    bool DeletionKnown,
    IReadOnlyList<ClaudePruneFloor> PruneFloors);

/// <summary>Whether the request store can be used.</summary>
internal enum ClaudeStoreStatus
{
    /// <summary>No store files: the one-time conversion has not happened (or the store was deleted).</summary>
    Absent,

    /// <summary>The store is open and its format is known.</summary>
    Available,

    /// <summary>
    /// Store files exist but cannot be used right now: damaged with no usable
    /// backup, an unknown format, or busy. Nothing is read from or written to it.
    /// </summary>
    Unavailable
}

/// <summary>Where one Claude sample goes, given the request store.</summary>
internal enum ClaudeSampleRoute
{
    /// <summary>Today's month-file rules: bucketize, larger bucket wins.</summary>
    Legacy,

    /// <summary>Its identity is a mark: it counts as the mark, at the mark's place and values.</summary>
    Mark,

    /// <summary>Its identity is a record: the record is its only representation.</summary>
    Recorded,

    /// <summary>Unknown identity logged at or after the conversion: it becomes a record.</summary>
    New,

    /// <summary>
    /// Logged at or after the conversion while the store cannot be used:
    /// shown from the log, never written anywhere.
    /// </summary>
    Held,

    /// <summary>
    /// The store cannot be used and the conversion instant is unknown:
    /// reports use today's rules, syncs write nothing for it.
    /// </summary>
    Unclassified
}

/// <summary>
/// What the request store holds at one moment, as a sync or a report sees it
/// while holding the history lock.
/// </summary>
internal sealed record ClaudeRequestView(
    ClaudeStoreStatus Status,
    long? FreezeMs,
    IReadOnlyDictionary<long, ClaudeRequestRow> Requests,
    IReadOnlyDictionary<string, UsageHistoryAccount> Accounts)
{
    private static readonly IReadOnlyDictionary<long, ClaudeRequestRow> NoRequests = new Dictionary<long, ClaudeRequestRow>();
    private static readonly IReadOnlyDictionary<string, UsageHistoryAccount> NoAccounts = new Dictionary<string, UsageHistoryAccount>();

    /// <summary>No store: every sample follows today's rules.</summary>
    public static readonly ClaudeRequestView Absent = new(ClaudeStoreStatus.Absent, null, NoRequests, NoAccounts);

    /// <summary>A store that exists but cannot be used, converted at <paramref name="freezeMs"/> when known.</summary>
    public static ClaudeRequestView Unavailable(long? freezeMs) =>
        new(ClaudeStoreStatus.Unavailable, freezeMs, NoRequests, NoAccounts);

    /// <summary>
    /// True when <paramref name="timestamp"/> falls in a whole second before
    /// the conversion's second. Compared on whole seconds because the parse
    /// cache keeps timestamps to the second: the same request must land on
    /// the same side whether it was parsed fresh or read from the cache. A
    /// request in the conversion's own second is on the record side.
    /// </summary>
    public static bool IsBeforeFreeze(DateTimeOffset timestamp, long freezeMs) =>
        timestamp.ToUnixTimeSeconds() < FreezeSecondOf(freezeMs);

    /// <summary>The whole Unix second the conversion falls in, rounded down.</summary>
    public static long FreezeSecondOf(long freezeMs) =>
        freezeMs >= 0 ? freezeMs / 1000 : -((-freezeMs + 999) / 1000);

    /// <summary>Where <paramref name="sample"/> goes.</summary>
    /// <param name="sample">A live or recovered sample.</param>
    /// <param name="row">The store row of its identity, for <see cref="ClaudeSampleRoute.Mark"/> and <see cref="ClaudeSampleRoute.Recorded"/>.</param>
    public ClaudeSampleRoute RouteOf(UsageSample sample, out ClaudeRequestRow? row)
    {
        row = null;
        if (sample.Provider != UsageProviderKind.Claude || sample.DedupKey == 0)
        {
            return ClaudeSampleRoute.Legacy;
        }

        switch (Status)
        {
            case ClaudeStoreStatus.Available:
                if (Requests.TryGetValue(sample.DedupKey, out row))
                {
                    return row.IsMark ? ClaudeSampleRoute.Mark : ClaudeSampleRoute.Recorded;
                }

                return IsBeforeFreeze(sample.Timestamp, FreezeMs!.Value) ? ClaudeSampleRoute.Legacy : ClaudeSampleRoute.New;

            case ClaudeStoreStatus.Unavailable:
                return FreezeMs is not { } freeze
                    ? ClaudeSampleRoute.Unclassified
                    : IsBeforeFreeze(sample.Timestamp, freeze) ? ClaudeSampleRoute.Legacy : ClaudeSampleRoute.Held;

            default:
                return ClaudeSampleRoute.Legacy;
        }
    }
}

/// <summary>
/// The SQLite file that keeps Claude requests by identity:
/// <c>history\claude-requests.db</c>. Every method must be called while the
/// caller holds the usage history's lock file, and every connection is opened
/// and disposed inside that hold, so no other connection is ever open while a
/// file here is replaced, restored or deleted.
/// </summary>
internal sealed class ClaudeRequestStore
{
    /// <summary>File name of the store, beside the month files.</summary>
    public const string FileName = "claude-requests.db";

    private const string SchemaVersion = "1";

    /// <summary>1: the identity is the FNV-1a hash of <c>message.id|requestId</c>, <see cref="UsageLogParser.StableKey"/>.</summary>
    private const string KeySchemeVersion = "1";

    private const int SqliteError = 1;
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private readonly string _directory;
    private readonly ILogger _logger;
    private bool _formatWarned;
    private bool _damageWarned;

    public ClaudeRequestStore(string directory, ILogger logger)
    {
        _directory = directory;
        _logger = logger;
    }

    private string MainPath => Path.Combine(_directory, FileName);

    private string BackupPath => MainPath + ".bak";

    private string BackupTempPath => MainPath + ".bak.tmp";

    private string CreateTempPath => MainPath + ".new";

    private string RestoreTempPath => MainPath + ".restore";

    /// <summary>True when the store or its backup exists, usable or not.</summary>
    public bool FilesPresent => File.Exists(MainPath) || File.Exists(BackupPath);

    /// <summary>
    /// True when any store file is left: the store, its journal files, the
    /// backup, temporary files or damaged copies set aside.
    /// </summary>
    public bool AnyFiles => Directory.Exists(_directory) && Directory.EnumerateFiles(_directory, FileName + "*").Any();

    /// <summary>
    /// Opens the store. A main file that fails its integrity check, or is
    /// missing beside a backup, is replaced by the backup with the deletion and
    /// prune points of <paramref name="barrierOf"/> applied again, so a backup
    /// taken before a delete or a prune never brings rows back.
    /// </summary>
    /// <param name="barrierOf">
    /// The history state's points, given the backup's own meta (the state may
    /// need it for its repair).
    /// </param>
    /// <param name="restore">False to never touch the files, for read-only callers.</param>
    public ClaudeStoreSession Open(Func<ClaudeStoreMeta?, ClaudeStoreBarrier> barrierOf, bool restore = true)
    {
        ArgumentNullException.ThrowIfNull(barrierOf);
        if (restore)
        {
            // Leftovers of a create or a restore that stopped halfway; no
            // connection can be open on them under the lock.
            DeleteDatabaseFiles(CreateTempPath);
            DeleteDatabaseFiles(RestoreTempPath);
        }

        if (File.Exists(MainPath))
        {
            var check = TryOpen(MainPath, out var connection, out var meta);
            switch (check)
            {
                case Check.Ok:
                    return new ClaudeStoreSession(this, connection!, meta!);
                case Check.UnknownFormat:
                    WarnFormatOnce();
                    return ClaudeStoreSession.Unavailable;
                case Check.Busy:
                    _logger.LogWarning("Usage history: the Claude request store could not be opened right now");
                    return ClaudeStoreSession.Unavailable;
            }

            if (!restore)
            {
                return ClaudeStoreSession.Unavailable;
            }
        }
        else if (!File.Exists(BackupPath))
        {
            return ClaudeStoreSession.Absent;
        }
        else if (!restore)
        {
            return ClaudeStoreSession.Unavailable;
        }

        return Restore(barrierOf);
    }

    /// <summary>
    /// Creates the store in one transaction: meta, the conversion's marks and
    /// the account names. It is built under a temporary name and moved into
    /// place only once committed, so a stopped conversion leaves no store.
    /// </summary>
    /// <returns>False when it could not be created; nothing is left behind then.</returns>
    public bool Create(ClaudeStoreMeta meta, IReadOnlyCollection<ClaudeRequestRow> marks, IEnumerable<UsageHistoryAccount> names)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(names);
        if (File.Exists(MainPath))
        {
            return false;
        }

        DeleteDatabaseFiles(CreateTempPath);
        try
        {
            using (var connection = Connect(CreateTempPath, SqliteOpenMode.ReadWriteCreate))
            {
                using (var transaction = connection.BeginTransaction())
                {
                    Execute(connection, transaction,
                        "CREATE TABLE meta(name TEXT PRIMARY KEY NOT NULL, value TEXT) WITHOUT ROWID;" +
                        "CREATE TABLE request(key INTEGER PRIMARY KEY NOT NULL, kind INTEGER NOT NULL, ts_ms INTEGER NOT NULL, " +
                        "bucket INTEGER NOT NULL, account TEXT NOT NULL, model TEXT NOT NULL, uncached INTEGER NOT NULL, " +
                        "cache_read INTEGER NOT NULL, write_5m INTEGER NOT NULL, write_1h INTEGER NOT NULL, " +
                        "output INTEGER NOT NULL, reasoning INTEGER NOT NULL, saved INTEGER NOT NULL) WITHOUT ROWID;" +
                        "CREATE INDEX request_ts ON request(ts_ms);" +
                        "CREATE TABLE account(key TEXT PRIMARY KEY NOT NULL, provider TEXT NOT NULL, name TEXT NOT NULL) WITHOUT ROWID;");

                    SetMeta(connection, transaction, "schema", SchemaVersion);
                    SetMeta(connection, transaction, "key_scheme", KeySchemeVersion);
                    SetMeta(connection, transaction, "freeze_ms", Text(meta.FreezeMs));
                    SetMeta(connection, transaction, "deletion_id", meta.DeletionId);
                    SetMeta(connection, transaction, "deleted_before_ms", Text(meta.DeletedBeforeMs));
                    // The first write comes after every prune the history
                    // already knows, so none of them can claim these rows.
                    var first = ClaudePruneFloor.HighestSeq(meta.Floors) + 1;
                    SetMeta(connection, transaction, "write_seq", Text(first));
                    SetMeta(connection, transaction, "prune_floors", ClaudePruneFloor.Format(meta.Floors));
                    InsertRows(connection, transaction, marks, saved: first);
                    UpsertNames(connection, transaction, names);
                    transaction.Commit();
                }

                Execute(connection, null, "PRAGMA journal_mode=WAL;");
            }

            DeleteJournalFiles(CreateTempPath);
            File.Move(CreateTempPath, MainPath);
            return true;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            _logger.LogWarning("Usage history: the Claude request store could not be created: {Error}", exception.GetType().Name);
            DeleteDatabaseFiles(CreateTempPath);
            return false;
        }
    }

    /// <summary>
    /// True when the store or its backup can be read and carries
    /// <paramref name="deletionId"/>: it was created after that delete, so a
    /// delete being finished again must leave it alone.
    /// </summary>
    public bool CarriesDeletionId(string deletionId)
    {
        foreach (var path in new[] { MainPath, BackupPath })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            if (TryOpen(path, out var connection, out var meta) == Check.Ok)
            {
                connection!.Dispose();
                if (string.Equals(meta!.DeletionId, deletionId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Deletes every store file: the store, its journal files, the backup,
    /// temporary files and damaged copies set aside.
    /// </summary>
    /// <returns>How many could not be removed.</returns>
    public int DeleteAllFiles()
    {
        if (!Directory.Exists(_directory))
        {
            return 0;
        }

        foreach (var path in Directory.EnumerateFiles(_directory, FileName + "*").ToList())
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Counted below.
            }
        }

        return Directory.EnumerateFiles(_directory, FileName + "*").Count();
    }

    /// <summary>
    /// Copies the store to <c>.bak</c> through a checked temporary file, when
    /// <paramref name="changed"/> or when the store is newer than its backup.
    /// </summary>
    internal void Backup(SqliteConnection connection, bool changed)
    {
        try
        {
            if (!changed && File.Exists(BackupPath) &&
                LastWrite(MainPath, MainPath + "-wal") <= File.GetLastWriteTimeUtc(BackupPath))
            {
                return;
            }

            DeleteDatabaseFiles(BackupTempPath);

            // Pending writes go into the main file first, so it is not newer
            // than the backup taken right after.
            Execute(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "VACUUM INTO $path;";
                command.Parameters.AddWithValue("$path", BackupTempPath);
                command.ExecuteNonQuery();
            }

            bool sound;
            using (var check = Connect(BackupTempPath, SqliteOpenMode.ReadWrite))
            {
                sound = QuickCheck(check);
            }

            DeleteJournalFiles(BackupTempPath);
            if (!sound)
            {
                _logger.LogWarning("Usage history: a new backup of the Claude request store failed its check and was discarded");
                DeleteDatabaseFiles(BackupTempPath);
                return;
            }

            File.Move(BackupTempPath, BackupPath, overwrite: true);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            _logger.LogWarning("Usage history: the Claude request store could not be backed up: {Error}", exception.GetType().Name);
            DeleteDatabaseFiles(BackupTempPath);
        }
    }

    // -- Opening and restoring ------------------------------------------------

    private enum Check
    {
        Ok,
        Damaged,
        UnknownFormat,
        Busy
    }

    private Check TryOpen(string path, out SqliteConnection? connection, out ClaudeStoreMeta? meta)
    {
        connection = null;
        meta = null;
        SqliteConnection? opened = null;
        try
        {
            opened = Connect(path, SqliteOpenMode.ReadWrite);
            var check = Validate(opened, out meta);
            if (check == Check.Ok)
            {
                connection = opened;
                opened = null;
            }

            return check;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            return Check.Damaged;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return Check.Busy;
        }
        finally
        {
            opened?.Dispose();
        }
    }

    private static Check Validate(SqliteConnection connection, out ClaudeStoreMeta? meta)
    {
        meta = null;
        try
        {
            if (!QuickCheck(connection))
            {
                return Check.Damaged;
            }

            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name, value FROM meta;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    values[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            }

            if (!values.TryGetValue("schema", out var schema) || schema != SchemaVersion ||
                !values.TryGetValue("key_scheme", out var scheme) || scheme != KeySchemeVersion ||
                ParseLong(values.GetValueOrDefault("freeze_ms")) is not { } freeze)
            {
                return Check.UnknownFormat;
            }

            meta = new ClaudeStoreMeta(
                freeze,
                values.GetValueOrDefault("deletion_id"),
                ParseLong(values.GetValueOrDefault("deleted_before_ms")),
                ParseLong(values.GetValueOrDefault("write_seq")) ?? 0,
                ClaudePruneFloor.Parse(values.GetValueOrDefault("prune_floors")));
            return Check.Ok;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            return Check.Damaged;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == SqliteError)
        {
            // A database without the expected tables is not this store.
            return Check.UnknownFormat;
        }
    }

    private ClaudeStoreSession Restore(Func<ClaudeStoreMeta?, ClaudeStoreBarrier> barrierOf)
    {
        if (!File.Exists(BackupPath))
        {
            WarnDamageOnce();
            return ClaudeStoreSession.Unavailable;
        }

        try
        {
            File.Copy(BackupPath, RestoreTempPath, overwrite: true);
            using (var connection = Connect(RestoreTempPath, SqliteOpenMode.ReadWrite))
            {
                var check = Validate(connection, out var meta);
                if (check != Check.Ok)
                {
                    connection.Dispose();
                    DeleteDatabaseFiles(RestoreTempPath);
                    if (check == Check.UnknownFormat)
                    {
                        WarnFormatOnce();
                    }
                    else
                    {
                        WarnDamageOnce();
                    }

                    return ClaudeStoreSession.Unavailable;
                }

                ApplyBarrier(connection, meta!, barrierOf(meta));
                Execute(connection, null, "PRAGMA journal_mode=WAL;");
            }

            DeleteJournalFiles(RestoreTempPath);

            // The damaged file and its journal files are kept for inspection;
            // a stray -wal must never be applied to the restored copy.
            var stamp = ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
            foreach (var path in new[] { MainPath, MainPath + "-wal", MainPath + "-shm" })
            {
                if (File.Exists(path))
                {
                    File.Move(path, path + stamp);
                }
            }

            File.Move(RestoreTempPath, MainPath);
            _logger.LogWarning("Usage history: the Claude request store was damaged and has been restored from its backup");
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            DeleteDatabaseFiles(RestoreTempPath);
            WarnDamageOnce();
            return ClaudeStoreSession.Unavailable;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            _logger.LogWarning("Usage history: the Claude request store could not be restored: {Error}", exception.GetType().Name);
            DeleteDatabaseFiles(RestoreTempPath);
            return ClaudeStoreSession.Unavailable;
        }

        return TryOpen(MainPath, out var restored, out var restoredMeta) == Check.Ok
            ? new ClaudeStoreSession(this, restored!, restoredMeta!)
            : ClaudeStoreSession.Unavailable;
    }

    /// <summary>
    /// Brings a backup up to the history's current deletion and prune points.
    /// The current retention setting is never applied here; only what a
    /// delete or a prune committed.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A backup with another deletion id than the state's known one
    /// predates a delete and is emptied. Only a delete makes a deletion id,
    /// so this holds even when the deletion point did not move (a second
    /// delete after the clock went back). A state that does not know its
    /// deletion id empties nothing.</item>
    /// <item>A committed prune removes only rows saved up to its write
    /// sequence, so a record saved again after the retention was extended
    /// survives a restore.</item>
    /// <item>No row stays below a deletion point the state is sure of (a
    /// rebuilt state's guess is not applied).</item>
    /// </list>
    /// </remarks>
    private static void ApplyBarrier(SqliteConnection connection, ClaudeStoreMeta meta, ClaudeStoreBarrier barrier)
    {
        using var transaction = connection.BeginTransaction();
        if (barrier.DeletionKnown && !string.Equals(barrier.DeletionId, meta.DeletionId, StringComparison.Ordinal))
        {
            Execute(connection, transaction, "DELETE FROM request; DELETE FROM account;");
            SetMeta(connection, transaction, "deletion_id", barrier.DeletionId);
            SetMeta(connection, transaction, "deleted_before_ms", Text(Max(barrier.DeletedBeforeMs, meta.DeletedBeforeMs)));
        }

        if (Max(barrier.DeletedBeforeMs, meta.DeletedBeforeMs) is { } deletedBeforeMs)
        {
            DeleteBelow(connection, transaction, deletedBeforeMs);
        }

        var floors = ClaudePruneFloor.Merge(barrier.PruneFloors, meta.Floors);
        foreach (var floor in floors)
        {
            DeleteBelow(connection, transaction, floor.FloorSeconds * 1000, savedUpTo: floor.Seq);
        }

        SetMeta(connection, transaction, "prune_floors", ClaudePruneFloor.Format(floors));

        // The backup's write sequence may be older than a prune the state
        // recorded since; the next write must come after every one of them,
        // or a prune would claim a row saved after it.
        SetMeta(connection, transaction, "write_seq", Text(Math.Max(meta.WriteSeq, ClaudePruneFloor.HighestSeq(floors))));
        transaction.Commit();
    }

    // -- Statements -----------------------------------------------------------

    /// <summary>
    /// Removes rows below a floor: records by their timestamp, marks by the
    /// start of their bucket, the same rule a month-file prune uses for the
    /// bucket the mark is counted in.
    /// </summary>
    /// <param name="savedUpTo">When set, only rows saved up to this write sequence.</param>
    internal static int DeleteBelow(SqliteConnection connection, SqliteTransaction transaction, long floorMs, long? savedUpTo = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "DELETE FROM request WHERE ((kind = 0 AND ts_ms < $floor) OR (kind = 1 AND bucket < $floorSeconds))" +
            (savedUpTo is null ? ";" : " AND saved <= $saved;");
        command.Parameters.AddWithValue("$floor", floorMs);
        command.Parameters.AddWithValue("$floorSeconds", CeilingSecondsOf(floorMs));
        if (savedUpTo is { } saved)
        {
            command.Parameters.AddWithValue("$saved", saved);
        }

        return command.ExecuteNonQuery();
    }

    /// <summary>Unix milliseconds rounded up to whole seconds.</summary>
    internal static long CeilingSecondsOf(long milliseconds) =>
        FloorDiv(milliseconds, 1000) + (milliseconds % 1000 == 0 ? 0 : 1);

    internal static void InsertRows(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<ClaudeRequestRow> rows, long saved, Action<int>? added = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT OR IGNORE INTO request(key, kind, ts_ms, bucket, account, model, uncached, cache_read, write_5m, write_1h, output, reasoning, saved) " +
            "VALUES($key, $kind, $ts, $bucket, $account, $model, $uncached, $read, $w5, $w1, $output, $reasoning, $saved);";
        var key = command.Parameters.Add("$key", SqliteType.Integer);
        var kind = command.Parameters.Add("$kind", SqliteType.Integer);
        var ts = command.Parameters.Add("$ts", SqliteType.Integer);
        var bucket = command.Parameters.Add("$bucket", SqliteType.Integer);
        var account = command.Parameters.Add("$account", SqliteType.Text);
        var model = command.Parameters.Add("$model", SqliteType.Text);
        var uncached = command.Parameters.Add("$uncached", SqliteType.Integer);
        var read = command.Parameters.Add("$read", SqliteType.Integer);
        var w5 = command.Parameters.Add("$w5", SqliteType.Integer);
        var w1 = command.Parameters.Add("$w1", SqliteType.Integer);
        var output = command.Parameters.Add("$output", SqliteType.Integer);
        var reasoning = command.Parameters.Add("$reasoning", SqliteType.Integer);
        command.Parameters.AddWithValue("$saved", saved);

        foreach (var row in rows)
        {
            key.Value = row.Key;
            kind.Value = row.IsMark ? 1 : 0;
            ts.Value = row.TimestampMs;
            bucket.Value = row.BucketStart;
            account.Value = row.Account;
            model.Value = row.Model;
            uncached.Value = row.Tokens.UncachedInputTokens;
            read.Value = row.Tokens.CacheReadInputTokens;
            w5.Value = row.Tokens.CacheWrite5mInputTokens;
            w1.Value = row.Tokens.CacheWrite1hInputTokens;
            output.Value = row.Tokens.OutputTokens;
            reasoning.Value = row.Tokens.ReasoningOutputTokens;
            var inserted = command.ExecuteNonQuery();
            added?.Invoke(inserted);
        }
    }

    internal static void UpsertNames(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<UsageHistoryAccount> names)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO account(key, provider, name) VALUES($key, $provider, $name) " +
            "ON CONFLICT(key) DO UPDATE SET provider = excluded.provider, name = excluded.name;";
        var key = command.Parameters.Add("$key", SqliteType.Text);
        var provider = command.Parameters.Add("$provider", SqliteType.Text);
        var name = command.Parameters.Add("$name", SqliteType.Text);
        foreach (var account in names)
        {
            key.Value = account.Key;
            provider.Value = account.Provider.ToString();
            name.Value = account.Name;
            command.ExecuteNonQuery();
        }
    }

    internal static void SetMeta(SqliteConnection connection, SqliteTransaction? transaction, string name, string? value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO meta(name, value) VALUES($name, $value) ON CONFLICT(name) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    internal static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool QuickCheck(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        using var reader = command.ExecuteReader();
        return reader.Read() && string.Equals(reader.GetString(0), "ok", StringComparison.Ordinal) && !reader.Read();
    }

    /// <summary>A connection that is never pooled, so disposing it closes the file.</summary>
    private static SqliteConnection Connect(string path, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = 30
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    // -- Files and helpers ----------------------------------------------------

    private void WarnFormatOnce()
    {
        if (!_formatWarned)
        {
            _formatWarned = true;
            _logger.LogWarning(
                "Usage history: the Claude request store has a format this version does not know; " +
                "new Claude usage is shown from the logs only and not saved");
        }
    }

    private void WarnDamageOnce()
    {
        if (!_damageWarned)
        {
            _damageWarned = true;
            _logger.LogWarning(
                "Usage history: the Claude request store and its backup are damaged; saved Claude requests are unavailable " +
                "and new ones are not saved until the damaged files are removed");
        }
    }

    private static DateTime LastWrite(string path, string journal) =>
        File.Exists(journal) && File.GetLastWriteTimeUtc(journal) > File.GetLastWriteTimeUtc(path)
            ? File.GetLastWriteTimeUtc(journal)
            : File.GetLastWriteTimeUtc(path);

    private static void DeleteJournalFiles(string path)
    {
        TryDelete(path + "-wal");
        TryDelete(path + "-shm");
        TryDelete(path + "-journal");
    }

    private static void DeleteDatabaseFiles(string path)
    {
        TryDelete(path);
        DeleteJournalFiles(path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Removed on a later attempt or by a delete.
        }
    }

    internal static bool IsRecoverable(Exception exception) =>
        exception is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException;

    internal static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    internal static string? Text(long? value) => value?.ToString(CultureInfo.InvariantCulture);

    internal static long? Max(long? left, long? right) =>
        left is null ? right : right is null ? left : Math.Max(left.Value, right.Value);

    private static long FloorDiv(long value, long divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);
}

/// <summary>
/// One use of the request store inside a hold of the history lock. Disposing
/// it closes the file.
/// </summary>
internal sealed class ClaudeStoreSession : IDisposable
{
    public static readonly ClaudeStoreSession Absent = new(ClaudeStoreStatus.Absent);
    public static readonly ClaudeStoreSession Unavailable = new(ClaudeStoreStatus.Unavailable);

    private static readonly long MinMs = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    private static readonly long MaxMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    private readonly ClaudeRequestStore? _store;
    private SqliteConnection? _connection;

    private ClaudeStoreSession(ClaudeStoreStatus status) => Status = status;

    internal ClaudeStoreSession(ClaudeRequestStore store, SqliteConnection connection, ClaudeStoreMeta meta)
    {
        _store = store;
        _connection = connection;
        Meta = meta;
        Status = ClaudeStoreStatus.Available;
    }

    public ClaudeStoreStatus Status { get; }

    /// <summary>The store's meta, when <see cref="Status"/> is available.</summary>
    public ClaudeStoreMeta? Meta { get; private set; }

    /// <summary>True once a write was committed in this session.</summary>
    public bool Changed { get; private set; }

    private SqliteConnection Connection =>
        _connection ?? throw new InvalidOperationException("The Claude request store is not open.");

    /// <summary>Every row, by identity. Rows with impossible values are skipped.</summary>
    public Dictionary<long, ClaudeRequestRow> ReadRequests()
    {
        var rows = new Dictionary<long, ClaudeRequestRow>();
        if (_connection is null)
        {
            return rows;
        }

        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT key, kind, ts_ms, bucket, account, model, uncached, cache_read, write_5m, write_1h, output, reasoning FROM request;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetInt64(0);
            var kind = reader.GetInt64(1);
            var ts = reader.GetInt64(2);
            var bucket = reader.GetInt64(3);
            if (key == 0 || kind is not (0 or 1) || ts < MinMs || ts > MaxMs ||
                bucket % UsageHistory.BucketSeconds != 0 || reader.IsDBNull(4) || reader.IsDBNull(5))
            {
                continue;
            }

            var tokens = new UsageTokens
            {
                UncachedInputTokens = reader.GetInt64(6),
                CacheReadInputTokens = reader.GetInt64(7),
                CacheWrite5mInputTokens = reader.GetInt64(8),
                CacheWrite1hInputTokens = reader.GetInt64(9),
                OutputTokens = reader.GetInt64(10),
                ReasoningOutputTokens = reader.GetInt64(11)
            };
            if (tokens.UncachedInputTokens < 0 || tokens.CacheReadInputTokens < 0 || tokens.CacheWrite5mInputTokens < 0 ||
                tokens.CacheWrite1hInputTokens < 0 || tokens.OutputTokens < 0 || tokens.ReasoningOutputTokens < 0)
            {
                continue;
            }

            rows[key] = new ClaudeRequestRow(
                key,
                kind == 1,
                ts,
                bucket,
                Intern(strings, reader.GetString(4)),
                Intern(strings, reader.GetString(5)),
                tokens);
        }

        return rows;
    }

    /// <summary>The stored name of every account with rows, by history key.</summary>
    public Dictionary<string, UsageHistoryAccount> ReadAccounts()
    {
        var accounts = new Dictionary<string, UsageHistoryAccount>(StringComparer.Ordinal);
        if (_connection is null)
        {
            return accounts;
        }

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT key, provider, name FROM account;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            if (string.IsNullOrEmpty(key) ||
                !Enum.TryParse<UsageProviderKind>(reader.GetString(1), ignoreCase: false, out var provider) ||
                !Enum.IsDefined(provider))
            {
                continue;
            }

            var name = reader.GetString(2);
            accounts[key] = new UsageHistoryAccount(key, provider, string.IsNullOrWhiteSpace(name) ? key : name);
        }

        return accounts;
    }

    /// <summary>
    /// Adds records (an identity already stored is left as it is: the first
    /// recorded copy wins) and updates account names, in one transaction.
    /// </summary>
    /// <returns>How many records were added.</returns>
    public int AddRecords(IReadOnlyCollection<ClaudeRequestRow> records, IReadOnlyCollection<UsageHistoryAccount> names)
    {
        if (records.Count == 0 && names.Count == 0)
        {
            return 0;
        }

        var added = 0;
        var saved = Meta!.WriteSeq + 1;
        using var transaction = Connection.BeginTransaction();
        ClaudeRequestStore.InsertRows(Connection, transaction, records, saved, count => added += count);
        ClaudeRequestStore.UpsertNames(Connection, transaction, names);
        ClaudeRequestStore.SetMeta(Connection, transaction, "write_seq", ClaudeRequestStore.Text(saved));
        transaction.Commit();
        Meta = Meta with { WriteSeq = saved };
        Changed = true;
        return added;
    }

    /// <summary>How many rows a <see cref="Prune"/> at <paramref name="cutoffMs"/> would remove.</summary>
    public long CountBelow(long cutoffMs)
    {
        using var command = Connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM request WHERE (kind = 0 AND ts_ms < $floor) OR (kind = 1 AND bucket < $floorSeconds);";
        command.Parameters.AddWithValue("$floor", cutoffMs);
        command.Parameters.AddWithValue("$floorSeconds", ClaudeRequestStore.CeilingSecondsOf(cutoffMs));
        return command.ExecuteScalar() is long count ? count : 0;
    }

    /// <summary>
    /// Moves the write sequence up to at least <paramref name="seq"/>, so the
    /// next write comes after every prune record that has seen it.
    /// </summary>
    public void AdvanceWriteSeq(long seq)
    {
        if (seq <= Meta!.WriteSeq)
        {
            return;
        }

        using var transaction = Connection.BeginTransaction();
        ClaudeRequestStore.SetMeta(Connection, transaction, "write_seq", ClaudeRequestStore.Text(seq));
        transaction.Commit();
        Meta = Meta with { WriteSeq = seq };
        Changed = true;
    }

    /// <summary>The prune entry a prune at <paramref name="floorSeconds"/> commits now: it covers every row saved so far.</summary>
    public ClaudePruneFloor PruneFloorAt(long floorSeconds) => new(Meta!.WriteSeq, floorSeconds);

    /// <summary>
    /// Removes the rows below <paramref name="floor"/> and records the prune
    /// in the same transaction, so a backup restored later can be trimmed to
    /// exactly what this prune removed.
    /// </summary>
    /// <returns>How many rows were removed.</returns>
    public int Prune(ClaudePruneFloor floor)
    {
        using var transaction = Connection.BeginTransaction();
        var removed = ClaudeRequestStore.DeleteBelow(Connection, transaction, floor.FloorSeconds * 1000);
        if (removed == 0)
        {
            transaction.Rollback();
            return 0;
        }

        var floors = ClaudePruneFloor.Merge(Meta!.Floors, [floor]);
        ClaudeRequestStore.SetMeta(Connection, transaction, "prune_floors", ClaudePruneFloor.Format(floors));
        transaction.Commit();
        Meta = Meta with { PruneFloors = floors };
        Changed = true;
        return removed;
    }


    /// <summary>Timestamp of the earliest record (not mark), or null when there is none.</summary>
    public long? EarliestRecordMs()
    {
        if (_connection is null)
        {
            return null;
        }

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT MIN(ts_ms) FROM request WHERE kind = 0;";
        var value = command.ExecuteScalar();
        return value is long ms && ms >= MinMs && ms <= MaxMs ? ms : null;
    }

    /// <summary>Backs the store up when it changed or is newer than its backup.</summary>
    public void Backup()
    {
        if (_connection is not null)
        {
            _store!.Backup(_connection, Changed);
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }

    private static string Intern(Dictionary<string, string> strings, string value)
    {
        if (strings.TryGetValue(value, out var existing))
        {
            return existing;
        }

        strings[value] = value;
        return value;
    }
}
