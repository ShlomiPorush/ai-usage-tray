namespace costats.Core.Pulse;

public sealed record ResetCredit(
    string Id,
    string ResetType,
    string? Title,
    string? Description,
    DateTimeOffset? GrantedAt,
    DateTimeOffset? ExpiresAt,
    long UsesLeft = 1)
{
    /// <summary>Reset types this app knows how to redeem.</summary>
    public const string CodexType = "codexRateLimits";
    public const string ClaudeType = "claudeRateLimits";

    public bool CanUseAt(DateTimeOffset now) =>
        ResetType is CodexType or ClaudeType && (ExpiresAt is null || ExpiresAt > now);
}

public sealed record ResetCreditBank(long AvailableCount, IReadOnlyList<ResetCredit>? Credits)
{
    public static ResetCreditBank Unknown { get; } = new(0, null);

    // A matching total alone is insufficient when a payload repeats an ID.
    // Codex credits are single-use rows; a Claude grant can carry several
    // uses, so the rows are compared against the count by their uses.
    public bool IsComplete => Credits is not null && AvailableCount >= 0 &&
        Credits.All(credit => credit.UsesLeft >= 1) &&
        Credits.Sum(credit => credit.UsesLeft) == AvailableCount &&
        Credits.All(credit => !string.IsNullOrWhiteSpace(credit.Id)) &&
        Credits.Select(credit => credit.Id).Distinct(StringComparer.Ordinal).Count() == Credits.Count;
}
