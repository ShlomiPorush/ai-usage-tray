using costats.Core.Alerts;

namespace costats.Application.Alerts;

/// <summary>Persists the last usage reading used by reset notifications.</summary>
public interface IUsageResetStateStore
{
    IReadOnlyList<UsageResetCheckpoint> Load();

    void Save(IReadOnlyCollection<UsageResetCheckpoint> checkpoints);
}
