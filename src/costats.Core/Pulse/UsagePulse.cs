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
}
