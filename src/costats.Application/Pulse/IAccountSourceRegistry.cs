namespace costats.Application.Pulse;

/// <summary>
/// Holds the per-account signal sources built from the current settings.
/// Unlike DI-registered sources, the set can be rebuilt at runtime so account
/// changes made in Settings apply on the next refresh without restarting.
/// </summary>
public interface IAccountSourceRegistry
{
    IReadOnlyList<ISignalSource> Current { get; }

    /// <summary>Rebuilds the source list from the current settings.</summary>
    void Reload();
}
