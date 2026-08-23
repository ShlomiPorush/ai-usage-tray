using costats.Application.Settings;
using costats.Core.Analytics;
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
    /// The accounts that have local logs, for an account picker. Codex profiles
    /// collapse into one merged entry.
    /// </summary>
    Task<IReadOnlyList<UsageAccountInfo>> GetAccountsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the in-memory scan so the next call re-reads the logs. The on-disk
    /// per-file cache is kept, so this is cheap.
    /// </summary>
    void Invalidate();
}

/// <summary>
/// Default <see cref="IUsageAnalyticsService"/>: a
/// <see cref="UsageLogCollector"/> behind a short-lived in-memory scan cache,
/// feeding <see cref="UsageAggregator"/>.
/// </summary>
/// <remarks>
/// Thread safety: one scan runs at a time behind a semaphore, and the scan
/// itself runs on the thread pool, so several callers can await concurrently
/// from any thread without duplicating the work.
/// </remarks>
public sealed class UsageAnalyticsService : IUsageAnalyticsService
{
    /// <summary>How long a scan is reused before the logs are read again.</summary>
    public static readonly TimeSpan DefaultScanFreshness = TimeSpan.FromMinutes(2);

    private readonly UsageLogCollector _collector;
    private readonly ILogger<UsageAnalyticsService> _logger;
    private readonly TimeSpan _freshness;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lazy<ModelPricingTable> _pricing;

    private UsageScanResult? _scan;
    private DateTimeOffset _scannedAt;

    /// <summary>Creates the service over the app's configured accounts.</summary>
    public UsageAnalyticsService(
        AppSettings settings,
        ILogger<UsageAnalyticsService>? logger = null)
        : this(
            new UsageLogCollector(() => (settings ?? throw new ArgumentNullException(nameof(settings))).GetLocalUsageAccounts()),
            new Lazy<ModelPricingTable>(() => ModelPricingLoader.Load()),
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
        : this(collector, new Lazy<ModelPricingTable>(() => pricing), freshness, logger)
    {
    }

    private UsageAnalyticsService(
        UsageLogCollector collector,
        Lazy<ModelPricingTable> pricing,
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

    /// <inheritdoc />
    public async Task<UsageReport> GetReportAsync(
        UsageDateRange range,
        IReadOnlyCollection<string>? accountIds = null,
        CancellationToken cancellationToken = default)
    {
        var scan = await GetScanAsync(cancellationToken).ConfigureAwait(false);

        var report = UsageAggregator.Aggregate(scan.Samples, new UsageAggregationOptions
        {
            Range = range,
            AccountIds = accountIds,
            Pricing = _pricing.Value,
            TimeZone = TimeZone,
            GeneratedAt = DateTimeOffset.UtcNow
        });

        return report with { Diagnostics = scan.Diagnostics };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UsageAccountInfo>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        var scan = await GetScanAsync(cancellationToken).ConfigureAwait(false);
        return scan.Accounts;
    }

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

            var scan = await _collector.CollectAsync(cancellationToken).ConfigureAwait(false);
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
