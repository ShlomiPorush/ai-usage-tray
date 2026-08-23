using costats.Core.Pulse;

namespace costats.Application.Pulse;

public interface IPulseOrchestrator
{
    IObservable<PulseState> PulseStream { get; }

    /// <summary>The latest complete provider snapshot, or null before the first successful refresh.</summary>
    PulseState? CurrentState { get; }

    Task RefreshOnceAsync(RefreshTrigger trigger, CancellationToken cancellationToken);

    /// <summary>
    /// Silently refresh a specific provider (no loading indicator).
    /// </summary>
    Task RefreshProviderAsync(string providerId, CancellationToken cancellationToken);

    void UpdateRefreshInterval(TimeSpan interval);
}
