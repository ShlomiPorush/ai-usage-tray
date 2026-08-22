namespace costats.Core.Pulse;

/// <summary>
/// How a provider itself rates one of its quota windows (Claude sends
/// <c>limits[].severity</c>). Reported through to the remote payload as-is.
/// It does not colour anything: every surface bands by the used number alone
/// (see <see cref="UsageBands"/>).
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
