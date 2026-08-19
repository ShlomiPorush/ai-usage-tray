using costats.Core.Pulse;

namespace costats.Application.Pulse;

public interface IPulseOrchestrator
{
    IObservable<PulseState> PulseStream { get; }

    Task RefreshOnceAsync(RefreshTrigger trigger, CancellationToken cancellationToken);

    /// <summary>
    /// Silently refresh a specific provider (no loading indicator).
    /// </summary>
    Task RefreshProviderAsync(string providerId, CancellationToken cancellationToken);

    void UpdateRefreshInterval(TimeSpan interval);
}
