namespace costats.Core.Pulse;

public sealed record UsagePulse(
    string ProviderId,
    DateTimeOffset CapturedAt,
    long? SessionUsed,
    long? SessionLimit,
    long? WeekUsed,
    long? WeekLimit,
    MonetaryBucket? SpendingBucket,
    ConsumptionDigest? Consumption,
    QuotaWindow? SessionWindow,
    QuotaWindow? WeekWindow)
{
    /// <summary>Model/surface-scoped quota windows (e.g. a per-model weekly limit); empty when none.</summary>
    public IReadOnlyList<ScopedQuota> ScopedQuotas { get; init; } = [];

    /// <summary>Provider-reported severity for the session window; null when the provider sends none.</summary>
    public QuotaSeverity? SessionSeverity { get; init; }

    /// <summary>Provider-reported severity for the weekly window; null when the provider sends none.</summary>
    public QuotaSeverity? WeekSeverity { get; init; }

    /// <summary>
    /// The provider says the account is being refused right now (Codex
    /// <c>rate_limit.limit_reached</c> / <c>allowed: false</c>). This is a
    /// separate signal from a window reading 100%, and only ever set from an
    /// account-wide limit: a spent per-model quota blocks that model, not the account.
    /// </summary>
    public bool IsBlocked { get; init; }
}
