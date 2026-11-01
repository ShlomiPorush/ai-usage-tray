using System.Collections.Concurrent;
using System.Diagnostics;
using costats.Application.Settings;
using costats.Core.Analytics;

namespace costats.Infrastructure.Analytics;

/// <summary>
/// Everything one scan produced.
/// </summary>
/// <param name="Samples">Deduplicated samples, ascending by timestamp.</param>
/// <param name="Accounts">Accounts that contributed at least one root.</param>
/// <param name="Diagnostics">Counters describing the scan itself.</param>
public sealed record UsageScanResult(
    IReadOnlyList<UsageSample> Samples,
    IReadOnlyList<UsageAccountInfo> Accounts,
    UsageScanDiagnostics Diagnostics)
{
    /// <summary>A scan that found nothing.</summary>
    public static readonly UsageScanResult Empty = new([], [], UsageScanDiagnostics.Empty);

    /// <summary>
    /// Last saved name of every removed account in <see cref="Accounts"/>, by
    /// its archived account id, as the user typed it (no "(removed)" mark).
    /// </summary>
    public IReadOnlyDictionary<string, string> ArchivedAccountNames { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Request identities counted by this scan, for usage-history recovery.</summary>
    public IReadOnlySet<long> DedupKeys { get; init; } = new HashSet<long>();

    /// <summary>Parse-cache entry names of every log file this scan listed.</summary>
    public IReadOnlySet<string> CacheEntryNames { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// True when every root listed completely and every file was read. Only a
    /// complete scan may be used to decide that a log file is gone.
    /// </summary>
    public bool IsComplete { get; init; } = true;

    /// <summary>Providers with a root that did not list completely or a file that could not be read.</summary>
    public IReadOnlySet<UsageProviderKind> IncompleteProviders { get; init; } = new HashSet<UsageProviderKind>();

    /// <summary>
    /// True when every root and file of <paramref name="provider"/> was read,
    /// whatever happened to the other providers.
    /// </summary>
    public bool IsCompleteFor(UsageProviderKind provider) => !IncompleteProviders.Contains(provider);
}

/// <summary>
/// Reads every configured agent log root and returns deduplicated usage samples.
/// This is the only IO in the usage engine.
/// </summary>
/// <remarks>
/// The scan runs entirely on the thread pool and never touches UI state, so it
/// is safe to await from a view model. Files are parsed in parallel and merged
/// in a stable path order, which makes the deduplicated result identical from
/// run to run.
/// </remarks>
public sealed class UsageLogCollector
{
    private readonly Func<IReadOnlyList<MonitoredAccountSettings>> _accountsProvider;
    private readonly Func<bool> _antigravityEnabled;
    private readonly AntigravityUsageLocations _antigravityLocations;
    private readonly UsageFileCache _cache;
    private readonly int _maxParallelism;

    /// <summary>
    /// Creates a collector.
    /// </summary>
    /// <param name="accountsProvider">
    /// Reads the current account list. Called once per scan so Settings edits
    /// take effect without a restart.
    /// </param>
    /// <param name="cache">
    /// Per-file parse cache, or <see cref="UsageFileCache.Disabled"/> to force a
    /// full parse. Defaults to the on-disk cache.
    /// </param>
    /// <param name="maxParallelism">Files parsed at once. Defaults to the CPU count, capped at 8.</param>
    /// <param name="antigravityEnabled">
    /// Reads whether Antigravity is turned on, once per scan. Antigravity is
    /// never scanned when null.
    /// </param>
    /// <param name="antigravityLocations">Where Antigravity keeps its conversations; the user profile when null.</param>
    public UsageLogCollector(
        Func<IReadOnlyList<MonitoredAccountSettings>> accountsProvider,
        UsageFileCache? cache = null,
        int? maxParallelism = null,
        Func<bool>? antigravityEnabled = null,
        AntigravityUsageLocations? antigravityLocations = null)
    {
        ArgumentNullException.ThrowIfNull(accountsProvider);
        _accountsProvider = accountsProvider;
        _antigravityEnabled = antigravityEnabled ?? (static () => false);
        _antigravityLocations = antigravityLocations ?? AntigravityUsageLocations.Default;
        _cache = cache ?? new UsageFileCache();
        _maxParallelism = Math.Max(1, maxParallelism ?? Math.Min(8, Environment.ProcessorCount));
    }

    public UsageCacheInfo GetCacheInfo() => _cache.Inspect();

    public void ClearCache() => _cache.Clear();

    /// <summary>
    /// Scans every root and returns the deduplicated samples.
    /// <para>
    /// Deduplication is global, not per file: Claude Code copies earlier turns
    /// into every resumed session file, so a request whose
    /// (message id, request id) pair was already counted is dropped no matter
    /// which file it turned up in. Skipping this roughly doubles every Claude
    /// number.
    /// </para>
    /// <para>
    /// Corrupt lines are counted in
    /// <see cref="UsageScanDiagnostics.SkippedLines"/> and a file that cannot be
    /// read at all is counted in
    /// <see cref="UsageScanDiagnostics.FilesFailed"/>. Neither is fatal.
    /// </para>
    /// </summary>
    public Task<UsageScanResult> CollectAsync(CancellationToken cancellationToken = default) =>
        CollectAsync(CurrentScope(), cancellationToken);

    /// <summary>
    /// Scans the roots of <paramref name="monitored"/> rather than the current
    /// settings, so a caller can use one account list for the scan and for
    /// whatever it does with the result. Antigravity follows the current setting.
    /// </summary>
    public Task<UsageScanResult> CollectAsync(
        IReadOnlyList<MonitoredAccountSettings> monitored,
        CancellationToken cancellationToken = default) =>
        CollectAsync(new UsageScanScope(monitored, _antigravityEnabled()), cancellationToken);

    /// <summary>The scope the current settings describe.</summary>
    public UsageScanScope CurrentScope() => new(_accountsProvider(), _antigravityEnabled());

    /// <summary>
    /// Scans exactly <paramref name="scope"/>, so a caller can use one scope
    /// for the scan and for whatever it does with the result.
    /// </summary>
    public async Task<UsageScanResult> CollectAsync(
        UsageScanScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var stopwatch = Stopwatch.StartNew();
        var roots = UsageLogRootResolver.Resolve(scope, _antigravityLocations);
        if (roots.Count == 0)
        {
            return UsageScanResult.Empty with
            {
                Diagnostics = UsageScanDiagnostics.Empty with { Duration = stopwatch.Elapsed }
            };
        }

        var accounts = roots
            .GroupBy(root => root.AccountId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new UsageAccountInfo(group.Key, group.First().DisplayName, group.First().Provider))
            .OrderBy(account => account.Provider)
            .ThenBy(account => account.AccountId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var jobs = EnumerateFiles(roots, out var incompleteRoots);
        var results = new ConcurrentDictionary<int, (UsageLogRoot Root, ParsedUsageFile Parsed)>();
        var failedProviders = new ConcurrentDictionary<UsageProviderKind, bool>();

        var filesParsed = 0;
        var filesFromCache = 0;
        var filesFailed = 0;
        var bytesParsed = 0L;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, jobs.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _maxParallelism,
                CancellationToken = cancellationToken
            },
            (index, token) =>
            {
                var job = jobs[index];
                try
                {
                    // A database takes its writes in a -wal file beside it, so
                    // its signature covers both and is taken before and after
                    // the read. A parse that raced a write is used, not cached.
                    var before = UsageFileSignature.Capture(job.File, job.Root.Provider);
                    var cached = _cache.TryRead(job.File, job.Root.Provider, before);
                    if (cached is not null)
                    {
                        Interlocked.Increment(ref filesFromCache);
                        results[index] = (job.Root, cached);
                        return ValueTask.CompletedTask;
                    }

                    var parsed = job.Root.Provider switch
                    {
                        UsageProviderKind.Claude => UsageLogParser.ParseClaudeFile(job.File.FullName, token),
                        UsageProviderKind.Codex => UsageLogParser.ParseCodexFile(job.File.FullName, token),
                        UsageProviderKind.Antigravity => AntigravityUsageParser.ParseFile(job.File.FullName, token),
                        _ => throw new InvalidOperationException("Unknown usage provider " + job.Root.Provider)
                    };

                    Interlocked.Increment(ref filesParsed);
                    Interlocked.Add(ref bytesParsed, job.File.Length);
                    if (UsageFileSignature.Capture(job.File, job.Root.Provider) == before)
                    {
                        _cache.Write(job.File, job.Root.Provider, parsed, before);
                    }

                    results[index] = (job.Root, parsed);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or NotSupportedException or
                        Microsoft.Data.Sqlite.SqliteException or InvalidDataException)
                {
                    // An unreadable or oversized conversation database fails
                    // on its own, like an unreadable log file; it never stops
                    // the scan of everything else.
                    Interlocked.Increment(ref filesFailed);
                    failedProviders[job.Root.Provider] = true;
                }

                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        var samples = new List<UsageSample>(results.Count * 8);
        var seen = new HashSet<long>();
        var duplicates = 0;
        var skippedLines = 0;

        for (var index = 0; index < jobs.Count; index++)
        {
            if (!results.TryGetValue(index, out var result))
            {
                continue;
            }

            skippedLines += result.Parsed.SkippedLines;
            foreach (var entry in result.Parsed.Entries)
            {
                if (entry.DedupKey != 0 && !seen.Add(entry.DedupKey))
                {
                    duplicates++;
                    continue;
                }

                samples.Add(new UsageSample(
                    entry.Timestamp,
                    result.Root.Provider,
                    result.Root.AccountId,
                    entry.Model,
                    entry.Tokens));
            }
        }

        samples.Sort(static (left, right) => left.Timestamp.CompareTo(right.Timestamp));

        var entryNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            entryNames.Add(UsageFileCache.EntryFileName(job.File.FullName, job.Root.Provider));
        }

        return new UsageScanResult(
            samples,
            accounts,
            new UsageScanDiagnostics
            {
                RootsScanned = roots.Count,
                FilesSeen = jobs.Count,
                FilesParsed = filesParsed,
                FilesFromCache = filesFromCache,
                FilesFailed = filesFailed,
                BytesParsed = bytesParsed,
                SkippedLines = skippedLines,
                DuplicatesDropped = duplicates,
                Duration = stopwatch.Elapsed
            })
        {
            DedupKeys = seen,
            CacheEntryNames = entryNames,
            IsComplete = incompleteRoots.Count == 0 && filesFailed == 0,
            IncompleteProviders = incompleteRoots.Concat(failedProviders.Keys).ToHashSet()
        };
    }

    /// <summary>
    /// Claude requests recovered from parse-cache entries whose log file this
    /// scan no longer lists. See <see cref="UsageFileCache.ReadOrphanedClaudeEntries"/>.
    /// </summary>
    public IReadOnlyList<RawUsageEntry> RecoverOrphanedClaudeEntries(
        UsageScanResult scan,
        DateTimeOffset before,
        out bool complete)
    {
        ArgumentNullException.ThrowIfNull(scan);
        return _cache.ReadOrphanedClaudeEntries(scan.CacheEntryNames, scan.DedupKeys, before, out complete);
    }

    /// <summary>
    /// Lists the log files under every root, in a stable order so that
    /// "first occurrence wins" deduplication is reproducible.
    /// </summary>
    /// <param name="roots">The roots to list.</param>
    /// <param name="incompleteRoots">
    /// The provider of every root or file that could not be listed or
    /// inspected. Empty when everything listed.
    /// </param>
    private static List<ScanJob> EnumerateFiles(IReadOnlyList<UsageLogRoot> roots, out List<UsageProviderKind> incompleteRoots)
    {
        var jobs = new List<ScanJob>();
        incompleteRoots = [];
        foreach (var root in roots.OrderBy(root => root.Provider).ThenBy(root => root.Path, StringComparer.OrdinalIgnoreCase))
        {
            var pattern = root.FilePattern;

            // Collected incrementally on purpose: an agent writes into these
            // trees while the scan runs, and a mid-enumeration IO error used to
            // throw away every file already listed for the root. Keeping the
            // partial list turns a transient hiccup into a few missing files
            // instead of a whole provider silently vanishing from the report.
            var files = new List<string>();
            try
            {
                foreach (var file in System.IO.Directory.EnumerateFiles(root.Path, pattern, SearchOption.AllDirectories))
                {
                    // Windows matches short-name patterns loosely; keep exact extensions only.
                    if (file.EndsWith(root.FileExtension, StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(file);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Keep what was listed before the failure.
                incompleteRoots.Add(root.Provider);
            }

            files.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                    if (!info.Exists || info.Length == 0)
                    {
                        continue;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // A file that could not even be inspected leaves the scan
                    // incomplete, the same as one that could not be read.
                    incompleteRoots.Add(root.Provider);
                    continue;
                }

                jobs.Add(new ScanJob(root, info));
            }
        }

        return jobs;
    }

    private readonly record struct ScanJob(UsageLogRoot Root, FileInfo File);
}
