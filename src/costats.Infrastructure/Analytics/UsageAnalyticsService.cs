using costats.Application.Settings;
using costats.Core.Analytics;
using costats.Core.Remote;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace costats.Infrastructure.Analytics;

/// <summary>
/// The usage analytics entry point. Scans the local agent logs and answers
/// questions about them.
/// </summary>
/// <remarks>
/// This is the whole surface a UI needs. A typical caller resolves the service
/// once, calls <see cref="GetAccountsAsync"/> to build a filter, then calls
/// <see cref="GetReportAsync"/> whenever the range or filter changes. Repeat
/// calls reuse the scanned samples for a short while, so changing the range is
/// instant and does not touch disk.
/// </remarks>
public interface IUsageAnalyticsService
{
    /// <summary>
    /// Builds a report for a range of local days, optionally restricted to some
    /// accounts.
    /// </summary>
    /// <param name="range">
    /// Local calendar days to include. Use <see cref="UsageDateRange.All"/> for
    /// everything the logs hold.
    /// </param>
    /// <param name="accountIds">
    /// Accounts to include, from <see cref="GetAccountsAsync"/>. Null or empty
    /// means every account.
    /// </param>
    /// <param name="cancellationToken">Cancels an in-flight scan.</param>
    Task<UsageReport> GetReportAsync(
        UsageDateRange range,
        IReadOnlyCollection<string>? accountIds = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a report for an exact rolling window, with hourly buckets, such
    /// as the past 24 hours.
    /// </summary>
    /// <param name="window">Instants to include.</param>
    /// <param name="accountIds">As for <see cref="GetReportAsync"/>.</param>
    /// <param name="cancellationToken">Cancels an in-flight scan.</param>
    Task<UsageReport> GetWindowReportAsync(
        UsageTimeWindow window,
        IReadOnlyCollection<string>? accountIds = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The accounts that have local logs, for an account picker. Codex profiles
    /// collapse into one merged entry.
    /// </summary>
    Task<IReadOnlyList<UsageAccountInfo>> GetAccountsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The earliest and latest local day any matching account has usage on, or
    /// null when there is none. A period that starts before the earliest day
    /// may be missing data whose logs were already deleted, and an empty range
    /// can name the days that do hold usage.
    /// </summary>
    /// <param name="accountIds">As for <see cref="GetReportAsync"/>.</param>
    /// <param name="cancellationToken">Cancels an in-flight scan.</param>
    Task<UsageActivityDays?> GetActivityDaysAsync(
        IReadOnlyCollection<string>? accountIds = null,
        CancellationToken cancellationToken = default);

    /// <summary>The time zone whose calendar days the reports bucket by.</summary>
    TimeZoneInfo TimeZone { get; }

    /// <summary>
    /// What the remote usage report is composed from: the same combined
    /// samples (after the deletion floor), prices and accounts the reports
    /// use, from the same scan cache.
    /// </summary>
    Task<RemoteUsageSource> GetRemoteUsageSourceAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the current on-disk parse cache size.</summary>
    UsageCacheInfo GetCacheInfo();

    /// <summary>Clears memory and disk caches. The next report performs a cold scan.</summary>
    Task<UsageCacheInfo> ClearCacheAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the in-memory scan so the next call re-reads the logs. The on-disk
    /// per-file cache is kept, so this is cheap.
    /// </summary>
    void Invalidate();

    /// <summary>
    /// Raised after the stored usage history was deleted, so open views reload
    /// instead of showing usage that no longer exists.
    /// </summary>
    event EventHandler? HistoryChanged;

    /// <summary>
    /// Scans the logs and saves their usage into the local history, so it is
    /// kept after the agents delete the logs. Also removes history older than
    /// the retention setting. Does nothing when history is not configured.
    /// </summary>
    Task SyncHistoryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the stored usage history. Usage before now stops appearing even
    /// while its logs are still on disk.
    /// </summary>
    /// <returns>False when the history was busy or some files could not be removed yet.</returns>
    Task<bool> DeleteHistoryAsync(CancellationToken cancellationToken = default);

    /// <summary>Size and span of the stored usage history.</summary>
    UsageHistoryInfo GetHistoryInfo();
}

/// <summary>
/// Where the usage history lives and what it needs to know about the app.
/// </summary>
/// <param name="Store">The on-disk history.</param>
/// <param name="Accounts">The configured accounts, read on every use.</param>
/// <param name="RetentionDays">
/// Days of history to keep, read on every use; 0 keeps everything.
/// </param>
public sealed record UsageHistoryOptions(
    UsageHistoryStore Store,
    Func<IReadOnlyList<MonitoredAccountSettings>> Accounts,
    Func<int> RetentionDays)
{
    /// <summary>Whether Antigravity is turned on, read on every use. Off when not set.</summary>
    public Func<bool> AntigravityEnabled { get; init; } = static () => false;

    /// <summary>
    /// The scope one scan and its history mapping share. The accounts are
    /// copied, so an edit in Settings while the scan runs cannot change the
    /// folder or name the scan's usage is filed under.
    /// </summary>
    public UsageScanScope Scope() => new(
        [.. (Accounts() ?? []).Select(account => account is null
            ? null!
            : new MonitoredAccountSettings
            {
                Id = account.Id,
                Type = account.Type,
                DisplayName = account.DisplayName,
                ConfigDir = account.ConfigDir,
                KeepSessionActive = account.KeepSessionActive
            })],
        AntigravityEnabled());
}

/// <summary>
/// Default <see cref="IUsageAnalyticsService"/>: a
/// <see cref="UsageLogCollector"/> behind a short-lived in-memory scan cache,
/// combined with the local usage history when <see cref="History"/> is set,
/// feeding <see cref="UsageAggregator"/>.
/// </summary>
/// <remarks>
/// Thread safety: one scan runs at a time behind a semaphore, and the scan
/// itself runs on the thread pool, so several callers can await concurrently
/// from any thread without duplicating the work. History writes run behind the
/// same semaphore and take the history's file lock inside it, never the other
/// way round, so the two locks cannot deadlock.
/// </remarks>
public sealed class UsageAnalyticsService : IUsageAnalyticsService
{
    /// <summary>How long a scan is reused before the logs are read again.</summary>
    public static readonly TimeSpan DefaultScanFreshness = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Syncs that may postpone the one-time cache recovery before it is given
    /// up: eight syncs, six hours apart, is two days.
    /// </summary>
    public const int MaxRecoveryAttempts = 8;

    private readonly UsageLogCollector _collector;
    private readonly ILogger<UsageAnalyticsService> _logger;
    private readonly TimeSpan _freshness;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<CancellationToken, Task<ModelPricingTable>> _pricing;

    private UsageScanResult? _scan;
    private DateTimeOffset _scannedAt;
    private int _generation;

    /// <inheritdoc />
    public event EventHandler? HistoryChanged;

    /// <summary>Creates the service over the app's configured accounts.</summary>
    public UsageAnalyticsService(
        AppSettings settings,
        ModelPricingProvider pricing,
        ILogger<UsageAnalyticsService>? logger = null)
        : this(
            new UsageLogCollector(
                () => (settings ?? throw new ArgumentNullException(nameof(settings))).GetEffectiveAccounts(),
                antigravityEnabled: () => settings!.AntigravityEnabled),
            (pricing ?? throw new ArgumentNullException(nameof(pricing))).GetAsync,
            DefaultScanFreshness,
            logger)
    {
    }

    /// <summary>Creates the service over an explicit collector and pricing table.</summary>
    public UsageAnalyticsService(
        UsageLogCollector collector,
        ModelPricingTable pricing,
        TimeSpan? freshness = null,
        ILogger<UsageAnalyticsService>? logger = null)
        : this(collector, _ => Task.FromResult(pricing ?? throw new ArgumentNullException(nameof(pricing))), freshness, logger)
    {
    }

    private UsageAnalyticsService(
        UsageLogCollector collector,
        Func<CancellationToken, Task<ModelPricingTable>> pricing,
        TimeSpan? freshness,
        ILogger<UsageAnalyticsService>? logger)
    {
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(pricing);
        _collector = collector;
        _pricing = pricing;
        _freshness = freshness ?? DefaultScanFreshness;
        _logger = logger ?? NullLogger<UsageAnalyticsService>.Instance;
    }

    /// <summary>The time zone whose calendar days the reports bucket by.</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>The local usage history, or null to report from the logs alone.</summary>
    public UsageHistoryOptions? History { get; init; }

    /// <inheritdoc />
    public Task<UsageReport> GetReportAsync(
        UsageDateRange range,
        IReadOnlyCollection<string>? accountIds = null,
        CancellationToken cancellationToken = default) =>
        AggregateAsync(range, null, accountIds, cancellationToken);

    /// <inheritdoc />
    public Task<UsageReport> GetWindowReportAsync(
        UsageTimeWindow window,
        IReadOnlyCollection<string>? accountIds = null,
        CancellationToken cancellationToken = default) =>
        AggregateAsync(UsageDateRange.All, window, accountIds, cancellationToken);

    private async Task<UsageReport> AggregateAsync(
        UsageDateRange range,
        UsageTimeWindow? window,
        IReadOnlyCollection<string>? accountIds,
        CancellationToken cancellationToken)
    {
        // A history delete can land while this report is being built. The
        // report is rebuilt when that happens, so it never shows deleted usage.
        for (var attempt = 0; ; attempt++)
        {
            var generation = Volatile.Read(ref _generation);
            var scan = await GetScanAsync(cancellationToken).ConfigureAwait(false);
            var pricing = await _pricing(cancellationToken).ConfigureAwait(false);

            var report = UsageAggregator.Aggregate(scan.Samples, new UsageAggregationOptions
            {
                Range = range,
                Window = window,
                AccountIds = accountIds,
                Pricing = pricing,
                TimeZone = TimeZone,
                GeneratedAt = DateTimeOffset.UtcNow
            });

            if (generation == Volatile.Read(ref _generation) || attempt >= 2)
            {
                return report with { Diagnostics = scan.Diagnostics };
            }
        }
    }

    /// <inheritdoc />
    public async Task<RemoteUsageSource> GetRemoteUsageSourceAsync(CancellationToken cancellationToken = default)
    {
        // Same rule as the reports: rebuilt when a history delete lands
        // meanwhile, so deleted usage is never shared.
        for (var attempt = 0; ; attempt++)
        {
            var generation = Volatile.Read(ref _generation);
            var scan = await GetScanAsync(cancellationToken).ConfigureAwait(false);
            var pricing = await _pricing(cancellationToken).ConfigureAwait(false);

            if (generation == Volatile.Read(ref _generation) || attempt >= 2)
            {
                return new RemoteUsageSource(scan.Samples, pricing, [.. scan.Accounts.Select(account => ShareAccountOf(account, scan))]);
            }
        }
    }

    /// <summary>
    /// The account as the remote report may name it. A removed account saved
    /// without a nickname was saved under its id, which is not a name.
    /// </summary>
    private static RemoteUsageAccountSource ShareAccountOf(UsageAccountInfo account, UsageScanResult scan)
    {
        if (account.AccountId.Equals(UsageHistory.ArchivedAccountIdPrefix + UsageHistory.UnknownClaudeAccountKey, StringComparison.Ordinal))
        {
            return new RemoteUsageAccountSource(account.AccountId, account.Provider, null, RemoteUsageAccountKind.UnknownHistory);
        }

        if (scan.ArchivedAccountNames.TryGetValue(account.AccountId, out var savedName))
        {
            var nickname = RemoteUsageComposer.LooksLikeAccountId(savedName) ? null : savedName;
            return new RemoteUsageAccountSource(account.AccountId, account.Provider, nickname, RemoteUsageAccountKind.Removed);
        }

        if (account.AccountId.StartsWith(UsageHistory.ArchivedAccountIdPrefix, StringComparison.Ordinal))
        {
            return new RemoteUsageAccountSource(account.AccountId, account.Provider, null, RemoteUsageAccountKind.Removed);
        }

        // A configured account without a nickname shows its id; the composer
        // treats a name equal to the id as no name.
        return new RemoteUsageAccountSource(account.AccountId, account.Provider, account.DisplayName);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UsageAccountInfo>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        var scan = await GetScanAsync(cancellationToken).ConfigureAwait(false);
        return scan.Accounts;
    }

    /// <inheritdoc />
    public async Task<UsageActivityDays?> GetActivityDaysAsync(
        IReadOnlyCollection<string>? accountIds = null,
        CancellationToken cancellationToken = default)
    {
        var scan = await GetScanAsync(cancellationToken).ConfigureAwait(false);
        var filter = accountIds is { Count: > 0 }
            ? new HashSet<string>(accountIds, StringComparer.OrdinalIgnoreCase)
            : null;

        DateTimeOffset? earliest = null;
        DateTimeOffset? latest = null;
        foreach (var sample in scan.Samples)
        {
            if (filter is not null && !filter.Contains(sample.AccountId))
            {
                continue;
            }

            if (earliest is null || sample.Timestamp < earliest)
            {
                earliest = sample.Timestamp;
            }

            if (latest is null || sample.Timestamp > latest)
            {
                latest = sample.Timestamp;
            }
        }

        return earliest is { } first && latest is { } last
            ? new UsageActivityDays(LocalDay(first), LocalDay(last))
            : null;
    }

    private DateOnly LocalDay(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, TimeZone).DateTime);

    /// <inheritdoc />
    public void Invalidate()
    {
        _gate.Wait();
        try
        {
            _scan = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public UsageCacheInfo GetCacheInfo() => _collector.GetCacheInfo();

    /// <inheritdoc />
    public UsageHistoryInfo GetHistoryInfo()
    {
        try
        {
            return History?.Store.Inspect() ?? UsageHistoryInfo.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return UsageHistoryInfo.Empty;
        }
    }

    /// <inheritdoc />
    public async Task SyncHistoryAsync(CancellationToken cancellationToken = default)
    {
        var history = History;
        if (history is null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // One scope for the scan and the mapping: a folder changed in
            // Settings mid-scan must not file old usage under the new key.
            var scope = history.Scope();
            var accounts = scope.Accounts;

            // The deletion point this sync scans against, read before the
            // scan: a delete that lands while the logs are read makes the
            // write below save nothing, so deleted usage never comes back.
            var start = history.Store.BeginSync();
            if (start is null)
            {
                return;
            }

            var scanStart = DateTimeOffset.UtcNow;
            var live = await _collector.CollectAsync(scope, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var map = HistoryAccountMap.Build(scope);
            var stored = history.Store.Read();
            var retentionDays = history.RetentionDays();
            var notBefore = NotBefore(start.DeletedBefore, retentionDays, now);

            // The one-time recovery of older Claude usage from the parse cache
            // runs only when every Claude root and file was read, with every
            // Claude folder present: a log the scan could not reach would
            // otherwise look deleted and be counted twice once it is back.
            // Other providers do not hold it up: a broken Antigravity database
            // must not use up the attempts and lose the Claude recovery.
            //
            // It is all or nothing: until it succeeds, nothing at all is saved,
            // so a later attempt never meets usage it would count twice. The
            // logs stay on disk meanwhile, and any that Claude Code deletes in
            // between are still in the cache. After MaxRecoveryAttempts
            // postponed syncs (two days) it is given up and saving starts.
            var claudeComplete = live.IsCompleteFor(UsageProviderKind.Claude) && AllClaudeLogsReachable(accounts);
            var recoveryDone = false;
            IReadOnlyList<UsageSample> recoveredSamples = [];
            if (history.Store.NeedsCacheRecovery())
            {
                var ready = claudeComplete &&
                    RecoverClaudeUsage(live, history.Store, now, out recoveredSamples);

                if (ready)
                {
                    recoveryDone = true;
                }
                else if (history.Store.RecordPostponedCacheRecovery() is var attempts &&
                         (attempts is null || attempts < MaxRecoveryAttempts))
                {
                    // A busy history (null) postpones too; it never counts as giving up.
                    _logger.LogInformation(
                        "Usage history: recovery of earlier usage postponed ({Attempts} of {Max}); some logs or cache files are out of reach",
                        attempts,
                        MaxRecoveryAttempts);
                    return;
                }
                else
                {
                    _logger.LogWarning("Usage history: recovery of earlier usage given up; saving current usage only");
                    recoveryDone = true;
                    recoveredSamples = [];
                }
            }

            var outcome = history.Store.Save(
                start,
                view => PlanHistoryWrite(view, live, recoveredSamples, map, notBefore, scanStart, recoveryDone, claudeComplete));
            if (outcome is null || outcome.Skipped)
            {
                return;
            }

            var pruned = 0;
            if (retentionDays > 0 && ClockLooksRight(now, stored, live, outcome.NewestRecordMs))
            {
                pruned = history.Store.Prune(now.AddDays(-retentionDays)) ?? 0;
            }

            // The scan just taken is as fresh as a report scan, so reports reuse it.
            _scan = CombineWithHistory(live, scope);
            _scannedAt = DateTimeOffset.UtcNow;
            if (outcome.Changed > 0 || outcome.RecordsAdded > 0 || outcome.Converted || pruned > 0)
            {
                Interlocked.Increment(ref _generation);
            }

            _logger.LogInformation(
                "Usage history sync: {Changed} buckets saved, {Records} Claude requests saved, {Pruned} expired buckets and requests removed",
                outcome.Changed,
                outcome.RecordsAdded,
                pruned);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Decides what one sync writes, given what the Claude request store holds.
    /// </summary>
    /// <remarks>
    /// Every sample other than a Claude request with an identity follows the
    /// month-file rules exactly as before (bucketize, larger bucket wins). A
    /// Claude request with an identity is attributed in this order:
    /// <list type="number">
    /// <item>A mark (counted in a bucket at the conversion): it takes part in
    /// the larger-wins comparison as the mark, at the mark's bucket, account
    /// and model and with the mark's tokens, never the copy's, so a moved or
    /// changed copy can neither move nor inflate it.</item>
    /// <item>A record: ignored, the record is its only representation.</item>
    /// <item>Unknown and logged before the conversion: the month-file rules.</item>
    /// <item>Unknown and logged at or after it: saved as a record, once; the
    /// first recorded copy wins.</item>
    /// </list>
    /// Until the conversion has committed, every sample follows the month-file
    /// rules; the conversion itself saves them that way and then marks every
    /// Claude request it just saved.
    /// </remarks>
    private static HistoryWritePlan PlanHistoryWrite(
        ClaudeRequestView view,
        UsageScanResult live,
        IReadOnlyList<UsageSample> recovered,
        HistoryAccountMap map,
        DateTimeOffset? notBefore,
        DateTimeOffset scanStart,
        bool recoveryDone,
        bool claudeComplete)
    {
        var legacy = new List<UsageSample>(live.Samples.Count);
        var marked = new List<UsageSample>();
        var unknownLegacy = new List<UsageSample>();
        var records = new List<ClaudeRequestRow>();

        void Route(UsageSample sample, Func<string, string?> keyOf, List<UsageSample> legacyTarget)
        {
            switch (view.RouteOf(sample, out var row))
            {
                case ClaudeSampleRoute.Legacy:
                    legacyTarget.Add(sample);
                    break;
                case ClaudeSampleRoute.Mark:
                    marked.Add(row!.ToSample(row.Account));
                    break;
                case ClaudeSampleRoute.New:
                    if ((notBefore is not { } floor || sample.Timestamp >= floor) && keyOf(sample.AccountId) is { } key)
                    {
                        records.Add(ClaudeRequestRow.From(sample, key, isMark: false));
                    }

                    break;
                default:
                    // Recorded: the record stands for it. Held or unclassified:
                    // the store cannot be used, and saving it as a bucket could
                    // count it twice once the store is back.
                    break;
            }
        }

        foreach (var sample in live.Samples)
        {
            Route(sample, map.KeyOf, legacy);
        }

        foreach (var sample in recovered)
        {
            Route(sample, static id => id, unknownLegacy);
        }

        var buckets = UsageHistory.Bucketize(legacy, map.KeyOf, notBefore);
        AddBuckets(buckets, UsageHistory.Bucketize(marked, static id => id, notBefore));
        AddBuckets(buckets, UsageHistory.Bucketize(unknownLegacy, static id => id, notBefore));

        UsageHistoryAccount? NameOf(string key) =>
            key == UsageHistory.UnknownClaudeAccountKey
                ? new UsageHistoryAccount(key, UsageProviderKind.Claude, UsageHistory.UnknownClaudeAccountName)
                : map.AccountFor(key);

        var names = buckets.Keys
            .Select(key => key.AccountKey)
            .Distinct(StringComparer.Ordinal)
            .Select(NameOf)
            .OfType<UsageHistoryAccount>()
            .ToList();

        ClaudeConversion? conversion = null;
        if (view.Status == ClaudeStoreStatus.Absent && claudeComplete)
        {
            // Every Claude request with an identity this sync saves becomes a
            // mark, with the bucket, account, model and tokens it is saved under.
            var marks = new List<ClaudeRequestRow>();
            foreach (var sample in live.Samples)
            {
                if (sample is { Provider: UsageProviderKind.Claude, DedupKey: not 0 } &&
                    (notBefore is not { } floor || sample.Timestamp >= floor) &&
                    map.KeyOf(sample.AccountId) is { } key)
                {
                    marks.Add(ClaudeRequestRow.From(sample, key, isMark: true));
                }
            }

            foreach (var sample in unknownLegacy)
            {
                if (sample.DedupKey != 0 && (notBefore is not { } floor || sample.Timestamp >= floor))
                {
                    marks.Add(ClaudeRequestRow.From(sample, sample.AccountId, isMark: true));
                }
            }

            // Rounded down to the whole second, the parse cache's resolution,
            // so every request the cache can present after the conversion is
            // on the record side. A request this scan read is a mark either way.
            conversion = new ClaudeConversion(scanStart.ToUnixTimeSeconds() * 1000, marks);
        }

        // Names of every account the store holds rows for, kept current.
        var storeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in view.Requests.Values)
        {
            storeKeys.Add(row.Account);
        }

        foreach (var row in records)
        {
            storeKeys.Add(row.Account);
        }

        foreach (var row in conversion?.Marks ?? [])
        {
            storeKeys.Add(row.Account);
        }

        var storeNames = storeKeys
            .Select(NameOf)
            .OfType<UsageHistoryAccount>()
            .ToList();

        return new HistoryWritePlan(buckets, names, recoveryDone, records, storeNames, conversion);
    }

    private static void AddBuckets(
        Dictionary<UsageHistoryKey, UsageHistoryBucket> target,
        Dictionary<UsageHistoryKey, UsageHistoryBucket> source)
    {
        foreach (var (key, bucket) in source)
        {
            target[key] = target.TryGetValue(key, out var existing)
                ? existing.Add(bucket.Tokens, bucket.Requests)
                : bucket;
        }
    }

    /// <summary>
    /// True when every configured Claude account's logs can be read: its
    /// folder exists, and its <c>projects</c> folder either exists or is
    /// simply absent (an account that never ran). A <c>projects</c> link whose
    /// target is gone, such as a disconnected drive, means logs are out of reach.
    /// </summary>
    private static bool AllClaudeLogsReachable(IReadOnlyList<MonitoredAccountSettings> accounts) =>
        accounts
            .Where(account => account is { IsValid: true, IsClaude: true })
            .All(account =>
            {
                if (!Directory.Exists(account.ConfigDir))
                {
                    return false;
                }

                var projects = Path.Combine(account.ConfigDir, UsageLogRootResolver.ClaudeLogFolder);
                if (Directory.Exists(projects))
                {
                    return true;
                }

                try
                {
                    return new DirectoryInfo(projects).LinkTarget is null && !File.Exists(projects);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    return false;
                }
            });

    /// <summary>
    /// Claude usage recovered from the parse cache, as samples under the
    /// "account unknown" history key, with their request identities. Requests
    /// a live log still holds are skipped by their request identity. Requests
    /// in a month that already has saved usage are skipped too: until the
    /// recovery is done, a month is only ever saved together with its
    /// recovered usage, so a saved month already holds everything it can, and
    /// taking more would count it twice. Identities the Claude request store
    /// already knows are skipped when the sync writes.
    /// </summary>
    /// <returns>True when every cache entry could be read.</returns>
    private bool RecoverClaudeUsage(
        UsageScanResult live,
        UsageHistoryStore store,
        DateTimeOffset now,
        out IReadOnlyList<UsageSample> samples)
    {
        // Months with a file on disk, readable or not: a saved month that is
        // only locked right now must still count as saved.
        var savedMonths = store.SavedMonths().ToHashSet(StringComparer.Ordinal);

        var recovered = _collector.RecoverOrphanedClaudeEntries(live, now, out var complete)
            .Where(entry => !savedMonths.Contains(UsageHistoryStore.MonthOf(entry.Timestamp.ToUnixTimeSeconds())))
            .Select(entry => new UsageSample(
                entry.Timestamp,
                UsageProviderKind.Claude,
                UsageHistory.UnknownClaudeAccountKey,
                entry.Model,
                entry.Tokens)
            {
                DedupKey = entry.DedupKey
            })
            .ToList();
        samples = recovered;
        if (recovered.Count > 0)
        {
            _logger.LogInformation("Usage history recovered {Count} earlier Claude requests from the parse cache", recovered.Count);
        }

        return complete;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteHistoryAsync(CancellationToken cancellationToken = default)
    {
        var history = History;
        if (history is null)
        {
            return false;
        }

        bool deleted;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            deleted = history.Store.DeleteAll(DateTimeOffset.UtcNow);
            _scan = null;
            Interlocked.Increment(ref _generation);
        }
        finally
        {
            _gate.Release();
        }

        if (deleted)
        {
            _logger.LogInformation("Usage history deleted");
        }

        // Raised either way: even a partial delete moves the deletion point,
        // so open views must reload.
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return deleted;
    }

    /// <summary>
    /// Usage before the returned instant is hidden: deleted by the user, or
    /// older than the retention setting.
    /// </summary>
    private static DateTimeOffset? NotBefore(DateTimeOffset? deletedBefore, int retentionDays, DateTimeOffset now)
    {
        DateTimeOffset? retained = retentionDays > 0 ? now.AddDays(-retentionDays) : null;
        return (deletedBefore, retained) switch
        {
            ({ } deleted, { } kept) => deleted > kept ? deleted : kept,
            ({ } deleted, null) => deleted,
            (null, { } kept) => kept,
            _ => null
        };
    }

    /// <summary>
    /// Expiry deletes history for good, so it only runs while the clock agrees
    /// with the newest usage on record: not behind it, and not wildly ahead.
    /// A clock that looks wrong keeps everything until it is right again.
    /// </summary>
    private static bool ClockLooksRight(DateTimeOffset now, UsageHistorySnapshot stored, UsageScanResult live, long? newestRecordMs)
    {
        long? newest = live.Samples.Count > 0 ? live.Samples[^1].Timestamp.ToUnixTimeSeconds() : null;
        foreach (var key in stored.Buckets.Keys)
        {
            if (newest is null || key.BucketStart > newest)
            {
                newest = key.BucketStart;
            }
        }

        // Claude requests kept by identity count too: with the logs gone they
        // may be the newest usage on record.
        if (newestRecordMs is { } recordMs && (newest is null || recordMs / 1000 > newest))
        {
            newest = recordMs / 1000;
        }

        if (newest is not { } latest)
        {
            return true;
        }

        var newestInstant = DateTimeOffset.FromUnixTimeSeconds(latest);
        return now >= newestInstant && now <= newestInstant.AddDays(90);
    }

    /// <summary>Adds the stored history to a fresh log scan.</summary>
    private UsageScanResult CombineWithHistory(UsageScanResult live, UsageScanScope? scope)
    {
        var history = History;
        if (history is null)
        {
            return live;
        }

        UsageHistorySnapshot stored;
        ClaudeRequestView claude;
        try
        {
            var read = history.Store.ReadForReport();
            stored = read.Snapshot;
            claude = read.Claude;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Usage history could not be read; reporting from the logs alone: {Error}", exception.GetType().Name);
            return LogsAfterPendingDelete(live, history.Store);
        }

        var map = HistoryAccountMap.Build(scope ?? history.Scope());
        var notBefore = NotBefore(stored.DeletedBefore, history.RetentionDays(), DateTimeOffset.UtcNow);

        // A sample rebuilt from a mark carries its stored account key, which
        // may belong to a removed account; it must still meet its own bucket.
        string? KeyOf(string accountId) =>
            map.KeyOf(accountId) ??
            (accountId.StartsWith(UsageHistory.ArchivedAccountIdPrefix, StringComparison.Ordinal)
                ? accountId[UsageHistory.ArchivedAccountIdPrefix.Length..]
                : null);

        // The same attribution as a sync: marks stand in for their copies at
        // their own place and values, records replace their copies, and an
        // unknown request from after the conversion is shown from its log
        // until a sync records it. Every other sample follows today's rules.
        var legacy = new List<UsageSample>(live.Samples.Count);
        var added = new List<UsageSample>();
        foreach (var sample in live.Samples)
        {
            switch (claude.RouteOf(sample, out var row))
            {
                case ClaudeSampleRoute.Mark:
                    legacy.Add(row!.ToSample(map.IdOf(row.Account)));
                    break;
                case ClaudeSampleRoute.Recorded:
                    break;
                case ClaudeSampleRoute.New:
                case ClaudeSampleRoute.Held:
                    added.Add(sample);
                    break;
                default:
                    legacy.Add(sample);
                    break;
            }
        }

        var samples = UsageHistory.Combine(legacy, KeyOf, stored.Buckets, map.IdOf, notBefore);
        var storeAccountKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sample in legacy)
        {
            if (sample.AccountId.StartsWith(UsageHistory.ArchivedAccountIdPrefix, StringComparison.Ordinal) &&
                (notBefore is not { } floor || sample.Timestamp >= floor))
            {
                storeAccountKeys.Add(sample.AccountId[UsageHistory.ArchivedAccountIdPrefix.Length..]);
            }
        }

        // Records and new requests are added, never compared: a month bucket
        // after the conversion holds only other usage (requests without an
        // identity, or what an older version saved).
        foreach (var sample in added)
        {
            if (notBefore is not { } floor || sample.Timestamp >= floor)
            {
                samples.Add(sample);
            }
        }

        foreach (var row in claude.Requests.Values)
        {
            if (!row.IsMark && (notBefore is not { } floor || row.Timestamp >= floor))
            {
                samples.Add(row.ToSample(map.IdOf(row.Account)));
                storeAccountKeys.Add(row.Account);
            }
        }

        if (added.Count > 0 || storeAccountKeys.Count > 0 || claude.Requests.Count > 0)
        {
            samples.Sort(static (left, right) => left.Timestamp.CompareTo(right.Timestamp));
        }

        var accounts = live.Accounts.ToList();
        var archivedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var listed = new HashSet<string>(accounts.Select(account => account.AccountId), StringComparer.OrdinalIgnoreCase);
        foreach (var accountKey in stored.Buckets.Keys.Select(key => key.AccountKey).Concat(storeAccountKeys).Distinct(StringComparer.Ordinal))
        {
            var info = map.InfoFor(accountKey, stored.Accounts);
            if (info is not null && listed.Add(info.AccountId))
            {
                accounts.Add(info);
                if (info.AccountId.StartsWith(UsageHistory.ArchivedAccountIdPrefix, StringComparison.Ordinal) &&
                    stored.Accounts.TryGetValue(accountKey, out var known))
                {
                    archivedNames[info.AccountId] = known.Name;
                }
            }
        }

        accounts.Sort(static (left, right) =>
        {
            var byProvider = left.Provider.CompareTo(right.Provider);
            return byProvider != 0
                ? byProvider
                : string.Compare(left.AccountId, right.AccountId, StringComparison.OrdinalIgnoreCase);
        });

        return live with { Samples = samples, Accounts = accounts, ArchivedAccountNames = archivedNames };
    }

    /// <summary>
    /// The logs alone, without what a delete that is not finished yet removes:
    /// nothing before its point is shown.
    /// </summary>
    private static UsageScanResult LogsAfterPendingDelete(UsageScanResult live, UsageHistoryStore store)
    {
        DateTimeOffset? point;
        try
        {
            point = store.PendingDeletionPoint();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The journal cannot be read either: show nothing rather than
            // usage that may have been deleted.
            return live with { Samples = [] };
        }

        return point is { } floor
            ? live with { Samples = [.. live.Samples.Where(sample => sample.Timestamp >= floor)] }
            : live;
    }

    public async Task<UsageCacheInfo> ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _scan = null;
            _collector.ClearCache();
            return _collector.GetCacheInfo();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<UsageScanResult> GetScanAsync(CancellationToken cancellationToken)
    {
        var cached = _scan;
        if (cached is not null && DateTimeOffset.UtcNow - _scannedAt < _freshness)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _scan;
            if (cached is not null && DateTimeOffset.UtcNow - _scannedAt < _freshness)
            {
                return cached;
            }

            var scope = History?.Scope();
            var live = scope is null
                ? await _collector.CollectAsync(cancellationToken).ConfigureAwait(false)
                : await _collector.CollectAsync(scope, cancellationToken).ConfigureAwait(false);
            var scan = CombineWithHistory(live, scope);
            _scan = scan;
            _scannedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "Usage scan: {Roots} roots, {Files} files ({Parsed} parsed, {Cached} cached, {Failed} failed), " +
                "{Samples} samples, {Duplicates} duplicates dropped, {Skipped} bad lines, {Elapsed} ms",
                scan.Diagnostics.RootsScanned,
                scan.Diagnostics.FilesSeen,
                scan.Diagnostics.FilesParsed,
                scan.Diagnostics.FilesFromCache,
                scan.Diagnostics.FilesFailed,
                scan.Samples.Count,
                scan.Diagnostics.DuplicatesDropped,
                scan.Diagnostics.SkippedLines,
                (long)scan.Diagnostics.Duration.TotalMilliseconds);

            return scan;
        }
        finally
        {
            _gate.Release();
        }
    }
}
