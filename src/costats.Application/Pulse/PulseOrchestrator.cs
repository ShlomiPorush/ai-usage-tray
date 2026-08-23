using costats.Application.Abstractions;
using costats.Core.Pulse;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace costats.Application.Pulse;

public sealed class PulseOrchestrator : BackgroundService, IPulseOrchestrator
{
    private readonly IEnumerable<ISignalSource> _staticSources;
    private readonly IAccountSourceRegistry _accountSources;
    private readonly ISourceSelector _selector;
    private readonly IClock _clock;
    private readonly PulseBroadcaster _broadcaster;
    private readonly IPulseSnapshotWriter _snapshotWriter;
    private readonly ILogger<PulseOrchestrator> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _intervalLock = new();

    private TimeSpan _refreshInterval;
    private CancellationTokenSource? _timerCts;
    private PulseState? _lastState;
    private bool _hasSuccessfulLoad;

    public PulseOrchestrator(
        IEnumerable<ISignalSource> sources,
        IAccountSourceRegistry accountSources,
        ISourceSelector selector,
        IClock clock,
        PulseBroadcaster broadcaster,
        IPulseSnapshotWriter snapshotWriter,
        IOptions<PulseOptions> options,
        ILogger<PulseOrchestrator> logger)
    {
        _staticSources = sources;
        _accountSources = accountSources;
        _selector = selector;
        _clock = clock;
        _broadcaster = broadcaster;
        _snapshotWriter = snapshotWriter;
        _refreshInterval = options.Value.RefreshInterval;
        _logger = logger;
    }

    public IObservable<PulseState> PulseStream => _broadcaster;

    public PulseState? CurrentState => Volatile.Read(ref _lastState);

    // Account sources come from the registry so Settings edits apply on the
    // next refresh; static sources (Copilot, Z.AI) stay DI-registered.
    private IEnumerable<ISignalSource> AllSources() => _staticSources.Concat(_accountSources.Current);

    public void UpdateRefreshInterval(TimeSpan interval)
    {
        lock (_intervalLock)
        {
            _refreshInterval = interval;
            try { _timerCts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        _logger.LogInformation("Refresh interval updated to {Interval}", interval);
    }

    public async Task RefreshOnceAsync(RefreshTrigger trigger, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ShouldShowShimmer(trigger))
            {
                PublishRefreshing(trigger);
            }

            var byProvider = AllSources()
                .GroupBy(source => source.Profile.ProviderId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<ISignalSource>)group.ToList());

            var errors = new List<string>();

            // Keep provider reads sequential to avoid overlapping heavy file scans.
            var providerReads = new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase);
            foreach (var (providerId, providerSources) in byProvider)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reading = await _selector.SelectAsync(providerId, providerSources, cancellationToken).ConfigureAwait(false);
                providerReads[providerId] = reading;
            }

            // Nothing below this line may run for a refresh that was cancelled:
            // the reads would be incomplete and would overwrite the last good state.
            cancellationToken.ThrowIfCancellationRequested();

            var state = new PulseState(providerReads, _clock.UtcNow, errors, false, trigger);
            _lastState = state;
            _hasSuccessfulLoad = true;
            _broadcaster.Publish(state);
            await _snapshotWriter.WriteAsync(state, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is a normal outcome (shutdown, interval change). Keep the
            // last good state and publish nothing.
            _logger.LogDebug("Pulse refresh cancelled ({Trigger})", trigger);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pulse refresh failed");
            var keepRefreshing = trigger == RefreshTrigger.Initial && !_hasSuccessfulLoad;
            var baseState = _lastState ?? new PulseState(
                new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase),
                _clock.UtcNow,
                Array.Empty<string>(),
                keepRefreshing,
                trigger);

            var state = baseState with
            {
                LastRefresh = _clock.UtcNow,
                Errors = new List<string> { ex.Message },
                IsRefreshing = keepRefreshing,
                Trigger = trigger
            };

            if (!keepRefreshing)
            {
                _lastState ??= state;
            }

            _broadcaster.Publish(state);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task RefreshProviderAsync(string providerId, CancellationToken cancellationToken)
    {
        // Silent refresh - don't wait if another refresh is in progress
        if (!await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug("Skipping silent refresh for {ProviderId} - refresh already in progress", providerId);
            return;
        }

        try
        {
            var providerSources = AllSources()
                .Where(s => s.Profile.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (providerSources.Count == 0)
            {
                _logger.LogWarning("No sources found for provider {ProviderId}", providerId);
                return;
            }

            var reading = await _selector.SelectAsync(providerId, providerSources, cancellationToken).ConfigureAwait(false);

            // A cancelled read must not be merged into the published state.
            cancellationToken.ThrowIfCancellationRequested();

            // Merge with existing state
            var existingProviders = _lastState?.Providers
                ?? new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase);

            var updatedProviders = new Dictionary<string, ProviderReading>(existingProviders, StringComparer.OrdinalIgnoreCase)
            {
                [providerId] = reading
            };

            var state = new PulseState(updatedProviders, _clock.UtcNow, Array.Empty<string>(), false, RefreshTrigger.Silent);
            _lastState = state;
            _broadcaster.Publish(state);

            _logger.LogDebug("Silent refresh completed for {ProviderId}", providerId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelled silent refresh: keep the last good state, publish nothing.
            _logger.LogDebug("Silent refresh cancelled for {ProviderId}", providerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Silent refresh failed for {ProviderId}", providerId);
            // Silent refresh failures are non-blocking - don't propagate
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RefreshOnceAsync(RefreshTrigger.Initial, stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan currentInterval;
                lock (_intervalLock)
                {
                    currentInterval = _refreshInterval;
                    _timerCts?.Dispose();
                    _timerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                }

                try
                {
                    using var timer = new PeriodicTimer(currentInterval);
                    while (await timer.WaitForNextTickAsync(_timerCts.Token).ConfigureAwait(false))
                    {
                        await RefreshOnceAsync(RefreshTrigger.Scheduled, _timerCts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Timer was cancelled due to interval change, restart with new interval
                    _logger.LogDebug("Restarting timer with new interval");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "PulseOrchestrator crashed");
            throw;
        }
    }

    private bool ShouldShowShimmer(RefreshTrigger trigger)
    {
        return trigger == RefreshTrigger.Manual || (trigger == RefreshTrigger.Initial && !_hasSuccessfulLoad);
    }

    private void PublishRefreshing(RefreshTrigger trigger)
    {
        // Show last known good state with loading indicator
        var baseState = _lastState ?? new PulseState(
            new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase),
            _clock.UtcNow,
            Array.Empty<string>(),
            true,
            trigger);

        var refreshing = baseState with
        {
            IsRefreshing = true,
            Trigger = trigger,
            LastRefresh = _clock.UtcNow
        };

        _broadcaster.Publish(refreshing);
    }

    public override void Dispose()
    {
        lock (_intervalLock)
        {
            _timerCts?.Dispose();
            _timerCts = null;
        }

        _refreshGate.Dispose();
        base.Dispose();
    }
}
