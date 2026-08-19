namespace costats.Core.Pulse;

public sealed record PulseState(
    IReadOnlyDictionary<string, ProviderReading> Providers,
    DateTimeOffset LastRefresh,
    IReadOnlyList<string> Errors,
    bool IsRefreshing,
    RefreshTrigger Trigger);
