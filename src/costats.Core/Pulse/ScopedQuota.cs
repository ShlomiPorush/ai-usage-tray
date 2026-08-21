namespace costats.Core.Pulse;

/// <summary>
/// How a provider itself rates one of its quota windows. When a provider
/// reports this (Claude sends <c>limits[].severity</c>) it wins over our own
/// percentage thresholds, so a window is coloured the way the provider's own
/// UI colours it.
/// </summary>
public enum QuotaSeverity
{
    Normal,
    Warning,
    Critical
}

/// <summary>
/// A provider-reported quota window scoped to a specific model or surface,
/// e.g. Claude's model-specific weekly limit ("Fable") or a Codex entry from
/// <c>additional_rate_limits</c>. Reported alongside the account-wide
/// session/weekly windows.
/// </summary>
public sealed record ScopedQuota(
    string Label,
    string Group,
    long UsedPercent,
    DateTimeOffset? ResetsAt,
    bool IsActive)
{
    /// <summary>Provider-reported severity; null when the provider sends none.</summary>
    public QuotaSeverity? Severity { get; init; }
}
