using costats.Core.Pulse;

namespace costats.Application.Pulse;

public sealed record ProviderRefreshResult(bool Succeeded, ProviderReading? Reading)
{
    public static ProviderRefreshResult Success(ProviderReading reading) => new(true, reading);

    public static ProviderRefreshResult Failure() => new(false, null);
}

public interface IPulseOrchestrator
{
    IObservable<PulseState> PulseStream { get; }

    /// <summary>The most recently published provider state, if an initial refresh has completed.</summary>
    PulseState? CurrentState { get; }

    Task RefreshOnceAsync(RefreshTrigger trigger, CancellationToken cancellationToken);

    /// <summary>
    /// Silently performs a complete refresh only when the last complete refresh is stale.
    /// Returns immediately when another refresh is already in progress.
    /// </summary>
    Task RefreshIfStaleAsync(TimeSpan maxAge, CancellationToken cancellationToken);

    /// <summary>
    /// Silently refresh a specific provider (no loading indicator).
    /// </summary>
    Task RefreshProviderAsync(string providerId, CancellationToken cancellationToken);

    /// <summary>
    /// Waits for any in-flight refresh, then reads and publishes a specific provider.
    /// The result succeeds only when this call obtained a fresh provider reading.
    /// </summary>
    Task<ProviderRefreshResult> RefreshProviderForVerificationAsync(
        string providerId,
        CancellationToken cancellationToken);

    /// <summary>True when no complete refresh exists within <paramref name="maxAge"/>.</summary>
    bool IsFullRefreshStale(TimeSpan maxAge);

    /// <summary>Publishes the last state again without reading any provider.</summary>
    void RepublishLastState();

    void UpdateRefreshInterval(TimeSpan interval);
}
