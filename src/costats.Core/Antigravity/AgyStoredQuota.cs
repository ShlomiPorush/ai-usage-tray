using System.Text.Json;
using costats.Core.Pulse;

namespace costats.Core.Antigravity;

/// <summary>A stored quota that is still worth showing, and when it was read.</summary>
public sealed record AgyStoredQuotaReading(AgyQuotaReading Quota, DateTimeOffset CompletedAt);

/// <summary>
/// The last successful Antigravity quota, kept in <c>last-quota.json</c> so a
/// restarted app can show it while the probe schedule still waits. It holds
/// the plan and the buckets only; the account email is never part of an
/// <see cref="AgyQuotaReading"/>.
/// </summary>
public static class AgyStoredQuota
{
    public const int Version = 1;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(AgyQuotaReading quota, DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(quota);
        var file = new StoredFile(
            Version,
            completedAt.ToUniversalTime(),
            quota.PlanTier,
            ToStored(quota.GeminiWeekly),
            ToStored(quota.GeminiFiveHour),
            quota.OtherQuotas.Select(other => new StoredScoped(other.Label, other.Group, other.UsedPercent, other.ResetsAt, other.IsActive)).ToList());
        return JsonSerializer.Serialize(file, Options);
    }

    /// <summary>
    /// The stored quota with only its still-current windows (see
    /// <see cref="RestoredUsage"/>), or null when the text is not a known
    /// version, is stamped in the future, or has nothing current left.
    /// </summary>
    public static AgyStoredQuotaReading? Parse(string? json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        StoredFile? file;
        try
        {
            file = JsonSerializer.Deserialize<StoredFile>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (file is null || file.Version != Version || RestoredUsage.IsFromTheFuture(file.CompletedAt, now))
        {
            return null;
        }

        var completedAt = file.CompletedAt;
        var weekly = Current(file.GeminiWeekly, completedAt, now);
        var fiveHour = Current(file.GeminiFiveHour, completedAt, now);
        var others = (file.Other ?? [])
            .Where(other => other.Label is not null && other.Group is not null &&
                            other.UsedPercent is >= 0 and <= 100 &&
                            RestoredUsage.IsCurrent(other.ResetsAt, completedAt, now))
            .Select(other => new ScopedQuota(other.Label!, other.Group!, other.UsedPercent, other.ResetsAt, other.IsActive))
            .ToList();

        if (weekly is null && fiveHour is null && others.Count == 0)
        {
            return null;
        }

        return new AgyStoredQuotaReading(new AgyQuotaReading(file.Plan, weekly, fiveHour, others), completedAt);
    }

    private static StoredBucket? ToStored(AgyQuotaBucket? bucket) =>
        bucket is null ? null : new StoredBucket(bucket.Family, bucket.Window, bucket.UsedPercent, bucket.ResetsAt);

    private static AgyQuotaBucket? Current(StoredBucket? bucket, DateTimeOffset completedAt, DateTimeOffset now) =>
        bucket is { Family: not null, Window: not null, UsedPercent: >= 0 and <= 100 } &&
        RestoredUsage.IsCurrent(bucket.ResetsAt, completedAt, now)
            ? new AgyQuotaBucket(bucket.Family, bucket.Window, bucket.UsedPercent, bucket.ResetsAt)
            : null;

    private sealed record StoredFile(
        int Version,
        DateTimeOffset CompletedAt,
        string? Plan,
        StoredBucket? GeminiWeekly,
        StoredBucket? GeminiFiveHour,
        IReadOnlyList<StoredScoped>? Other);

    private sealed record StoredBucket(string? Family, string? Window, long UsedPercent, DateTimeOffset? ResetsAt);

    private sealed record StoredScoped(string? Label, string? Group, long UsedPercent, DateTimeOffset? ResetsAt, bool IsActive);
}
