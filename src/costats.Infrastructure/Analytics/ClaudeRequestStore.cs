using System.Globalization;
using costats.Core.Analytics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace costats.Infrastructure.Analytics;

/// <param name="Key">The request identity, <see cref="UsageSample.DedupKey"/>; never 0.</param>
/// <param name="IsMark">
/// True for a mark: a request that was already counted in a month-file bucket
/// when the store was created, and stays counted there. False for a record:
/// a request first seen after that, counted from this row alone.
/// </param>
/// <param name="BucketStart">Unix seconds, not milliseconds, of the 15-minute bucket.</param>
/// <param name="Account">The history account key, not the account id.</param>
/// <param name="Model">Normalized the way a bucket stores it.</param>
internal sealed record ClaudeRequestRow(
    long Key,
    bool IsMark,
    long TimestampMs,
    long BucketStart,
    string Account,
    string Model,
    UsageTokens Tokens)
{
    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs);

    public static ClaudeRequestRow From(UsageSample sample, string account, bool isMark) => new(
        sample.DedupKey,
        isMark,
        sample.Timestamp.ToUnixTimeMilliseconds(),
        UsageHistory.BucketStartOf(sample.Timestamp),
        account,
        UsageHistory.NormalizeModel(sample.Model),
        sample.Tokens);

    public UsageSample ToSample(string accountId) =>
        new(Timestamp, UsageProviderKind.Claude, accountId, Model, Tokens) { DedupKey = Key };
}

/// <param name="FreezeMs">
/// The conversion instant: requests logged at or after it are saved as
/// records, requests before it follow the month-file rules.
/// </param>
internal sealed record ClaudeStoreMeta(long FreezeMs);

internal enum ClaudeStoreStatus
{
    /// <summary>No store files: the one-time conversion has not happened (or the store was deleted).</summary>
    Absent,

    Available,

    /// <summary>
    /// Store files exist but cannot be used right now: damaged with no usable
    /// backup, an unknown format, or busy. Nothing is read from or written to it.
    /// </summary>
    Unavailable
}

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

/// <summary>The request store as read under one hold of the history lock.</summary>
internal sealed record ClaudeRequestView(
    ClaudeStoreStatus Status,
    long? FreezeMs,
    IReadOnlyDictionary<long, ClaudeRequestRow> Requests)
{
    private static readonly IReadOnlyDictionary<long, ClaudeRequestRow> NoRequests = new Dictionary<long, ClaudeRequestRow>();

    public static readonly ClaudeRequestView Absent = new(ClaudeStoreStatus.Absent, null, NoRequests);

    public static ClaudeRequestView Unavailable(long? freezeMs) =>
        new(ClaudeStoreStatus.Unavailable, freezeMs, NoRequests);

    /// <summary>
    /// Compared on whole seconds because the parse cache keeps timestamps to
    /// the second: the same request must land on the same side whether it was
    /// parsed fresh or read from the cache. A request in the conversion's own
    /// second is on the record side.
    /// </summary>
    public static bool IsBeforeFreeze(DateTimeOffset timestamp, long freezeMs) =>
        timestamp.ToUnixTimeSeconds() < FreezeSecondOf(freezeMs);

    public static long FreezeSecondOf(long freezeMs) =>
        freezeMs >= 0 ? freezeMs / 1000 : -((-freezeMs + 999) / 1000);

    /// <param name="row">Set only for <see cref="ClaudeSampleRoute.Mark"/> and <see cref="ClaudeSampleRoute.Recorded"/>.</param>
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
/// Every method must be called while the caller holds the usage history's
/// lock file, and every connection is opened and disposed inside that hold,
/// so no other connection is ever open while a file here is replaced,
/// restored or deleted.
/// </summary>
internal sealed class ClaudeRequestStore
{
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

    public bool FilesPresent => File.Exists(MainPath) || File.Exists(BackupPath);

    /// <summary>
    /// A main file that fails its integrity check, or is missing beside a
    /// backup, is replaced by the backup. A backup a delete could not remove
    /// may hold rows from before that delete; the deletion point hides them.
    /// </summary>
    /// <param name="restore">False to skip the restore and the cleanup of leftovers, for callers that only read.</param>
    public ClaudeStoreSession Open(bool restore = true)
    {
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

        return Restore();
    }

    /// <summary>
    /// Built under a temporary name and moved into place only once committed,
    /// so a stopped conversion leaves no store.
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
                    SetMeta(connection, transaction, "freeze_ms", meta.FreezeMs.ToString(CultureInfo.InvariantCulture));
                    InsertRows(connection, transaction, marks);
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
    /// The records exist nowhere else once Claude Code deletes its logs, so a
    /// damaged store is replaced by this copy. It goes through a temporary
    /// file, so a copy cut short never replaces the previous backup.
    /// </summary>
    internal void Backup(SqliteConnection connection)
    {
        try
        {
            DeleteDatabaseFiles(BackupTempPath);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "VACUUM INTO $path;";
                command.Parameters.AddWithValue("$path", BackupTempPath);
                command.ExecuteNonQuery();
            }

            File.Move(BackupTempPath, BackupPath, overwrite: true);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            _logger.LogWarning("Usage history: the Claude request store could not be backed up: {Error}", exception.GetType().Name);
            DeleteDatabaseFiles(BackupTempPath);
        }
    }

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

            meta = new ClaudeStoreMeta(freeze);
            return Check.Ok;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == SqliteError)
        {
            // A database without the expected tables is not this store.
            return Check.UnknownFormat;
        }
    }

    private ClaudeStoreSession Restore()
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
                var check = Validate(connection, out _);
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

    // The saved column is no longer read; it stays so the schema needs no
    // migration and stores of earlier versions keep opening.
    /// <returns>How many rows were new.</returns>
    internal static int InsertRows(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<ClaudeRequestRow> rows)
    {
        var added = 0;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT OR IGNORE INTO request(key, kind, ts_ms, bucket, account, model, uncached, cache_read, write_5m, write_1h, output, reasoning, saved) " +
            "VALUES($key, $kind, $ts, $bucket, $account, $model, $uncached, $read, $w5, $w1, $output, $reasoning, 0);";
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
            added += command.ExecuteNonQuery();
        }

        return added;
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

    private static void SetMeta(SqliteConnection connection, SqliteTransaction transaction, string name, string? value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO meta(name, value) VALUES($name, $value);";
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


}

/// <summary>Must be disposed inside the hold of the history lock it was opened in.</summary>
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

    public ClaudeStoreMeta? Meta { get; }

    private SqliteConnection Connection =>
        _connection ?? throw new InvalidOperationException("The Claude request store is not open.");

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

    /// <summary>An identity already stored is left as it is: the first recorded copy wins.</summary>
    public int AddRecords(IReadOnlyCollection<ClaudeRequestRow> records, IReadOnlyCollection<UsageHistoryAccount> names)
    {
        if (records.Count == 0 && names.Count == 0)
        {
            return 0;
        }

        using var transaction = Connection.BeginTransaction();
        var added = ClaudeRequestStore.InsertRows(Connection, transaction, records);
        ClaudeRequestStore.UpsertNames(Connection, transaction, names);
        transaction.Commit();
        return added;
    }

    /// <summary>
    /// Marks go by the start of their bucket, not their timestamp: the same
    /// rule a month-file prune uses for the bucket the mark is counted in.
    /// </summary>
    public int Prune(long floorSeconds)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "DELETE FROM request WHERE (kind = 0 AND ts_ms < $floorMs) OR (kind = 1 AND bucket < $floorSeconds);";
        command.Parameters.AddWithValue("$floorMs", floorSeconds * 1000);
        command.Parameters.AddWithValue("$floorSeconds", floorSeconds);
        return command.ExecuteNonQuery();
    }

    public long? EarliestRecordMs(long? notBeforeMs)
    {
        if (_connection is null)
        {
            return null;
        }

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT MIN(ts_ms) FROM request WHERE kind = 0 AND ts_ms >= $notBefore;";
        command.Parameters.AddWithValue("$notBefore", notBeforeMs ?? long.MinValue);
        var value = command.ExecuteScalar();
        return value is long ms && ms >= MinMs && ms <= MaxMs ? ms : null;
    }

    public void Backup()
    {
        if (_connection is not null)
        {
            _store!.Backup(_connection);
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
