using costats.Application.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace costats.Infrastructure.Analytics;

/// <summary>
/// Saves the agents' log usage into the local usage history in the
/// background, so it is kept even when the Usage window is never opened before
/// Claude Code deletes a transcript (after 30 days by default).
/// </summary>
public sealed class UsageHistorySyncWorker : BackgroundService
{
    /// <summary>Wait after startup, so the first sync does not compete with the app starting.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    /// <summary>Time between syncs. Far shorter than any log retention.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly IUsageAnalyticsService _analytics;
    private readonly ILogger<UsageHistorySyncWorker> _logger;

    public UsageHistorySyncWorker(IUsageAnalyticsService analytics, ILogger<UsageHistorySyncWorker>? logger = null)
    {
        _analytics = analytics ?? throw new ArgumentNullException(nameof(analytics));
        _logger = logger ?? NullLogger<UsageHistorySyncWorker>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _analytics.SyncHistoryAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    // The next sync retries; the logs stay on disk meanwhile.
                    _logger.LogWarning(
                        "Usage history sync failed: {ErrorKind}: {Error}",
                        exception.GetType().Name,
                        LogRedaction.Redact(exception.Message));
                }

                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
