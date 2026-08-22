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
    public UsageLogCollector(
        Func<IReadOnlyList<MonitoredAccountSettings>> accountsProvider,
        UsageFileCache? cache = null,
        int? maxParallelism = null)
    {
        ArgumentNullException.ThrowIfNull(accountsProvider);
        _accountsProvider = accountsProvider;
        _cache = cache ?? new UsageFileCache();
        _maxParallelism = Math.Max(1, maxParallelism ?? Math.Min(8, Environment.ProcessorCount));
    }

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
    public async Task<UsageScanResult> CollectAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var roots = UsageLogRootResolver.Resolve(_accountsProvider());
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

        var jobs = EnumerateFiles(roots).ToList();
        var results = new ConcurrentDictionary<int, (UsageLogRoot Root, ParsedUsageFile Parsed)>();

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
                    var cached = _cache.TryRead(job.File, job.Root.Provider);
                    if (cached is not null)
                    {
                        Interlocked.Increment(ref filesFromCache);
                        results[index] = (job.Root, cached);
                        return ValueTask.CompletedTask;
                    }

                    var parsed = job.Root.Provider == UsageProviderKind.Claude
                        ? UsageLogParser.ParseClaudeFile(job.File.FullName, token)
                        : UsageLogParser.ParseCodexFile(job.File.FullName, token);

                    Interlocked.Increment(ref filesParsed);
                    Interlocked.Add(ref bytesParsed, job.File.Length);
                    _cache.Write(job.File, job.Root.Provider, parsed);
                    results[index] = (job.Root, parsed);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    Interlocked.Increment(ref filesFailed);
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
            });
    }

    /// <summary>
    /// Lists the log files under every root, in a stable order so that
    /// "first occurrence wins" deduplication is reproducible.
    /// </summary>
    private static IEnumerable<ScanJob> EnumerateFiles(IReadOnlyList<UsageLogRoot> roots)
    {
        foreach (var root in roots.OrderBy(root => root.Provider).ThenBy(root => root.Path, StringComparer.OrdinalIgnoreCase))
        {
            var pattern = root.Provider == UsageProviderKind.Codex ? "rollout-*.jsonl" : "*.jsonl";

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
                    files.Add(file);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Keep what was listed before the failure.
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
                    continue;
                }

                yield return new ScanJob(root, info);
            }
        }
    }

    private readonly record struct ScanJob(UsageLogRoot Root, FileInfo File);
}
