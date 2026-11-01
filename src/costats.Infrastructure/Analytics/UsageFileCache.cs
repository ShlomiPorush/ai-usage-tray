using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using costats.Core.Analytics;
using costats.Application.Storage;

namespace costats.Infrastructure.Analytics;

public sealed record UsageCacheInfo(int FileCount, long Bytes)
{
    public static readonly UsageCacheInfo Empty = new(0, 0);
}

/// <summary>
/// What a cached parse is checked against: a log file's length and last write
/// time, plus, for an Antigravity database, the same two of its <c>-wal</c>
/// file. SQLite commits into the WAL and leaves the database file alone until
/// a checkpoint, so the database alone would not show new calls.
/// </summary>
/// <param name="Length">Length of the file.</param>
/// <param name="LastWriteUtcTicks">Last write time of the file.</param>
/// <param name="WalLength">Length of the <c>-wal</c> file, -1 when there is none or it is not tracked.</param>
/// <param name="WalLastWriteUtcTicks">Last write time of the <c>-wal</c> file, 0 when there is none.</param>
public readonly record struct UsageFileSignature(
    long Length,
    long LastWriteUtcTicks,
    long WalLength,
    long WalLastWriteUtcTicks)
{
    /// <summary>
    /// The signature of <paramref name="file"/> now. Claude and Codex logs use
    /// the values the scan listed; an Antigravity database and its WAL are
    /// read fresh, so a write during the parse is noticed.
    /// </summary>
    public static UsageFileSignature Capture(FileInfo file, UsageProviderKind provider)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (provider != UsageProviderKind.Antigravity)
        {
            return new UsageFileSignature(file.Length, file.LastWriteTimeUtc.Ticks, -1, 0);
        }

        var database = new FileInfo(file.FullName);
        var wal = new FileInfo(file.FullName + "-wal");
        return wal.Exists
            ? new UsageFileSignature(database.Length, database.LastWriteTimeUtc.Ticks, wal.Length, wal.LastWriteTimeUtc.Ticks)
            : new UsageFileSignature(database.Length, database.LastWriteTimeUtc.Ticks, -1, 0);
    }
}

/// <summary>
/// Per-file parse cache for the usage scanner.
/// </summary>
/// <remarks>
/// A full first scan reads well over a gigabyte of JSONL. Almost all of it is
/// closed sessions that will never change again, so each file's parsed usage is
/// written to <c>%LOCALAPPDATA%\AIUsageTray\cache\usage\</c> and reused while the
/// file's <em>length</em> and <em>last write time</em> both still match. Only
/// new or changed files are re-parsed, which is what makes a repeat scan cheap.
/// <para>
/// Granularity is deliberately per file, not per scan: appending to one active
/// session invalidates that file alone.
/// </para>
/// <para>
/// Only counts, model ids, timestamps and opaque request-identity hashes are
/// stored. No message content and no account identity ever reaches the cache.
/// </para>
/// <para>
/// Thread safety: every file has its own cache entry and writes go through a
/// temporary file plus an atomic move, so concurrent parsing threads never
/// collide. A read that races a write simply misses and re-parses.
/// </para>
/// </remarks>
public sealed class UsageFileCache
{
    /// <summary>Bump to discard every previously written cache entry.</summary>
    private const int CacheSchemaVersion = 1;

    /// <summary>
    /// Schema of Antigravity entries. Kept apart from
    /// <see cref="CacheSchemaVersion"/> on purpose: the Claude history
    /// recovery and older app versions accept only schema 1, so they never
    /// mistake an Antigravity entry for Claude usage.
    /// </summary>
    private const int AntigravitySchemaVersion = 2;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };

    private readonly string? _directory;

    /// <summary>
    /// Creates a cache rooted at <paramref name="directory"/>, or at the default
    /// <c>%LOCALAPPDATA%\AIUsageTray\cache\usage</c> when null. Pass
    /// <see cref="Disabled"/> to skip caching entirely.
    /// </summary>
    public UsageFileCache(string? directory = null)
    {
        _directory = directory ?? DefaultDirectory();
    }

    private UsageFileCache(bool _)
    {
        _directory = null;
    }

    /// <summary>A cache that never stores or returns anything.</summary>
    public static UsageFileCache Disabled { get; } = new(false);

    /// <summary>Where entries are written, or null when caching is off.</summary>
    public string? Directory => _directory;

    public UsageCacheInfo Inspect()
    {
        if (_directory is null || !System.IO.Directory.Exists(_directory))
        {
            return UsageCacheInfo.Empty;
        }

        try
        {
            var files = 0;
            long bytes = 0;
            foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    bytes += new FileInfo(path).Length;
                    files++;
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    // A file that changes during inspection is omitted.
                }
            }

            return new UsageCacheInfo(files, bytes);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return UsageCacheInfo.Empty;
        }
    }

    /// <summary>The default cache location.</summary>
    public static string DefaultDirectory() => AppDataPaths.Combine("cache", "usage");

    /// <summary>
    /// Returns the cached parse for <paramref name="file"/> when one exists and
    /// still matches the file's length and last write time.
    /// </summary>
    public ParsedUsageFile? TryRead(FileInfo file, UsageProviderKind provider) =>
        TryRead(file, provider, UsageFileSignature.Capture(file, provider));

    /// <summary>
    /// Returns the cached parse for <paramref name="file"/> when one exists and
    /// still matches <paramref name="signature"/>.
    /// </summary>
    public ParsedUsageFile? TryRead(FileInfo file, UsageProviderKind provider, UsageFileSignature signature)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (_directory is null)
        {
            return null;
        }

        try
        {
            var path = EntryPath(file.FullName, provider);
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            var entry = JsonSerializer.Deserialize<CacheEntry>(stream, SerializerOptions);
            if (entry is null ||
                entry.Schema != SchemaOf(provider) ||
                entry.Parser != ParserVersionOf(provider) ||
                entry.Signature != signature)
            {
                return null;
            }

            return entry.ToParsed();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Stores the parse for <paramref name="file"/>. Failures are swallowed: a
    /// cache miss next time is the only consequence.
    /// </summary>
    public void Write(FileInfo file, UsageProviderKind provider, ParsedUsageFile parsed) =>
        Write(file, provider, parsed, UsageFileSignature.Capture(file, provider));

    /// <summary>
    /// Stores the parse for <paramref name="file"/> under the
    /// <paramref name="signature"/> it was read at. Failures are swallowed.
    /// </summary>
    public void Write(FileInfo file, UsageProviderKind provider, ParsedUsageFile parsed, UsageFileSignature signature)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(parsed);
        if (_directory is null)
        {
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            var path = EntryPath(file.FullName, provider);
            var temp = path + "." + Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture) + ".tmp";

            var entry = CacheEntry.From(signature, SchemaOf(provider), ParserVersionOf(provider), parsed);
            using (var stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, entry, SerializerOptions);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Caching is an optimisation; never fail a scan over it.
        }
    }

    /// <summary>Deletes every cached parse. The next scan is a cold scan.</summary>
    public void Clear()
    {
        if (_directory is null || !System.IO.Directory.Exists(_directory))
        {
            return;
        }

        try
        {
            System.IO.Directory.Delete(_directory, recursive: true);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Nothing to do; a stale cache is still validated per file.
        }
    }

    private static int SchemaOf(UsageProviderKind provider) =>
        provider == UsageProviderKind.Antigravity ? AntigravitySchemaVersion : CacheSchemaVersion;

    private static int ParserVersionOf(UsageProviderKind provider) => provider switch
    {
        UsageProviderKind.Claude or UsageProviderKind.Codex => UsageLogParser.FormatVersion,
        UsageProviderKind.Antigravity => AntigravityUsageParser.FormatVersion,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };

    private string EntryPath(string fullPath, UsageProviderKind provider) =>
        Path.Combine(_directory!, EntryFileName(fullPath, provider));

    /// <summary>File name of the cache entry for a log file.</summary>
    public static string EntryFileName(string fullPath, UsageProviderKind provider)
    {
        var key = provider + "|" + fullPath.ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(hash) + ".json";
    }

    /// <summary>
    /// Recovers Claude requests from cache entries whose log file is no longer
    /// part of the scan, typically because Claude Code deleted the transcript.
    /// </summary>
    /// <remarks>
    /// An entry keeps no path or account, so only what can be proven unique is
    /// returned: rows with a request identity (non-zero <c>DedupKey</c>, which
    /// only the Claude parser produces) that no live file already holds, each
    /// identity once. Rows without an identity, which includes all Codex rows,
    /// are left alone. Claude rows parse the same under parser versions 1 and
    /// 2 (version 2 changed only Codex), so both are accepted.
    /// </remarks>
    /// <param name="liveEntryNames">Entry file names of every log file the scan listed.</param>
    /// <param name="knownKeys">Request identities the scan already counted.</param>
    /// <param name="before">Only rows older than this instant are returned.</param>
    /// <param name="complete">
    /// False when an entry could not be opened (locked, say), so the caller
    /// can try again later. A damaged entry is skipped and does not count:
    /// it will never become readable.
    /// </param>
    public IReadOnlyList<RawUsageEntry> ReadOrphanedClaudeEntries(
        IReadOnlySet<string> liveEntryNames,
        IReadOnlySet<long> knownKeys,
        DateTimeOffset before,
        out bool complete)
    {
        ArgumentNullException.ThrowIfNull(liveEntryNames);
        ArgumentNullException.ThrowIfNull(knownKeys);

        complete = true;
        var recovered = new List<RawUsageEntry>();
        if (_directory is null || !System.IO.Directory.Exists(_directory))
        {
            return recovered;
        }

        var seen = new HashSet<long>();
        IEnumerable<string> paths;
        try
        {
            paths = System.IO.Directory.EnumerateFiles(_directory, "*.json")
                .Where(path => !liveEntryNames.Contains(Path.GetFileName(path)))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            complete = false;
            return recovered;
        }

        foreach (var path in paths)
        {
            IReadOnlyList<RawUsageEntry> rows;
            try
            {
                using var stream = File.OpenRead(path);
                var entry = JsonSerializer.Deserialize<CacheEntry>(stream, SerializerOptions);
                if (entry is null || entry.Schema != CacheSchemaVersion || entry.Parser is < 1 or > UsageLogParser.FormatVersion)
                {
                    continue;
                }

                rows = entry.ToParsed().Entries;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                complete = false;
                continue;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                continue;
            }

            foreach (var row in rows)
            {
                if (row.DedupKey != 0 &&
                    row.Timestamp < before &&
                    !knownKeys.Contains(row.DedupKey) &&
                    seen.Add(row.DedupKey))
                {
                    recovered.Add(row);
                }
            }
        }

        return recovered;
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException
            or ArgumentException;

    /// <summary>
    /// On-disk shape. Rows are positional arrays rather than objects so a large
    /// history stays a few megabytes: each row is
    /// <c>[dedupKey, unixSeconds, modelIndex, uncachedInput, cacheRead,
    /// cacheWrite5m, cacheWrite1h, output, reasoning]</c>.
    /// </summary>
    private sealed record CacheEntry
    {
        private const int RowWidth = 9;

        public int Schema { get; init; }
        public int Parser { get; init; }
        public long Length { get; init; }
        public long LastWriteUtcTicks { get; init; }

        /// <summary>WAL length of an Antigravity entry; absent (-1) in schema 1.</summary>
        public long WalLength { get; init; } = -1;

        /// <summary>WAL last write time of an Antigravity entry; absent (0) in schema 1.</summary>
        public long WalLastWriteUtcTicks { get; init; }

        public int Skipped { get; init; }

        [System.Text.Json.Serialization.JsonIgnore]
        public UsageFileSignature Signature => new(Length, LastWriteUtcTicks, WalLength, WalLastWriteUtcTicks);
        public string[] Models { get; init; } = [];
        public long[][] Rows { get; init; } = [];

        public static CacheEntry From(UsageFileSignature signature, int schema, int parser, ParsedUsageFile parsed)
        {
            var models = new List<string>();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            var rows = new long[parsed.Entries.Count][];

            for (var i = 0; i < parsed.Entries.Count; i++)
            {
                var source = parsed.Entries[i];
                if (!index.TryGetValue(source.Model, out var modelIndex))
                {
                    modelIndex = models.Count;
                    index[source.Model] = modelIndex;
                    models.Add(source.Model);
                }

                rows[i] =
                [
                    source.DedupKey,
                    source.Timestamp.ToUnixTimeSeconds(),
                    modelIndex,
                    source.Tokens.UncachedInputTokens,
                    source.Tokens.CacheReadInputTokens,
                    source.Tokens.CacheWrite5mInputTokens,
                    source.Tokens.CacheWrite1hInputTokens,
                    source.Tokens.OutputTokens,
                    source.Tokens.ReasoningOutputTokens
                ];
            }

            return new CacheEntry
            {
                Schema = schema,
                Parser = parser,
                Length = signature.Length,
                LastWriteUtcTicks = signature.LastWriteUtcTicks,
                WalLength = signature.WalLength,
                WalLastWriteUtcTicks = signature.WalLastWriteUtcTicks,
                Skipped = parsed.SkippedLines,
                Models = [.. models],
                Rows = rows
            };
        }

        public ParsedUsageFile ToParsed()
        {
            var entries = new List<RawUsageEntry>(Rows.Length);
            foreach (var row in Rows)
            {
                if (row is not { Length: RowWidth })
                {
                    continue;
                }

                var modelIndex = (int)row[2];
                if (modelIndex < 0 || modelIndex >= Models.Length)
                {
                    continue;
                }

                entries.Add(new RawUsageEntry(
                    row[0],
                    DateTimeOffset.FromUnixTimeSeconds(row[1]),
                    Models[modelIndex],
                    new UsageTokens
                    {
                        UncachedInputTokens = row[3],
                        CacheReadInputTokens = row[4],
                        CacheWrite5mInputTokens = row[5],
                        CacheWrite1hInputTokens = row[6],
                        OutputTokens = row[7],
                        ReasoningOutputTokens = row[8]
                    }));
            }

            return new ParsedUsageFile(entries, Skipped);
        }
    }
}
