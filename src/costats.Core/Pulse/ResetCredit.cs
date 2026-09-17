namespace costats.Core.Pulse;

public sealed record ResetCredit(
    string Id,
    string ResetType,
    string? Title,
    string? Description,
    DateTimeOffset? GrantedAt,
    DateTimeOffset? ExpiresAt)
{
    public bool CanUseAt(DateTimeOffset now) =>
        ResetType == "codexRateLimits" && (ExpiresAt is null || ExpiresAt > now);
}

public sealed record ResetCreditBank(long AvailableCount, IReadOnlyList<ResetCredit>? Credits)
{
    public static ResetCreditBank Unknown { get; } = new(0, null);

    // A matching count alone is insufficient when a payload repeats an ID.
    public bool IsComplete => Credits is not null && AvailableCount >= 0 &&
        Credits.Count == AvailableCount &&
        Credits.All(credit => !string.IsNullOrWhiteSpace(credit.Id)) &&
        Credits.Select(credit => credit.Id).Distinct(StringComparer.Ordinal).Count() == Credits.Count;
}
