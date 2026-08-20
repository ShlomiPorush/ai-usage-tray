namespace costats.Core.Pulse;

/// <summary>
/// A provider-reported quota window scoped to a specific model or surface,
/// e.g. Claude's model-specific weekly limit ("Fable"). Reported alongside the
/// account-wide session/weekly windows.
/// </summary>
public sealed record ScopedQuota(
    string Label,
    string Group,
    long UsedPercent,
    DateTimeOffset? ResetsAt,
    bool IsActive);
