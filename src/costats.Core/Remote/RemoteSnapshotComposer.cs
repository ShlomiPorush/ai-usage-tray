using System.Text.Json.Serialization;
using costats.Core.Pulse;

namespace costats.Core.Remote;

/// <summary>
/// One account to publish in a remote snapshot. The app layer fills these in
/// from the current pulse state; keeping the shape here means the composer has
/// no dependency on settings or WPF and stays unit testable.
/// </summary>
public sealed record RemoteSnapshotEntry(
    string ProviderId,
    string DisplayName,
    string Plan,
    UsagePulse? Usage);

/// <summary>
/// A single quota window as published to the remote viewer. <c>Scope</c> is the
/// model or surface the window is limited to (null for account-wide windows),
/// and <c>Severity</c> is the provider's own rating, null when it reports none.
/// </summary>
public sealed record RemoteWindowSnapshot(
    string Label,
    long UsedPercent,
    DateTimeOffset? ResetsAt,
    string? Scope = null,
    string? Severity = null);

/// <summary>
/// Redeemable "usage limit reset" credits on one account (Codex). Display only:
/// redeeming stays in the Codex CLI. <c>ExpiresAt</c> is null when the provider
/// gives no expiry.
/// </summary>
public sealed record RemoteResetCredits(long Available, DateTimeOffset? ExpiresAt);

/// <summary>One account in the published snapshot.</summary>
public sealed record RemoteAccountSnapshot(
    string Id,
    string Provider,
    string Name,
    string Plan,
    IReadOnlyList<RemoteWindowSnapshot> Windows,
    bool Blocked = false,
    // Additive on top of schema v2: the key is absent entirely for the accounts
    // and providers that have nothing to redeem, so old viewers are unaffected.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    RemoteResetCredits? ResetCredits = null);

/// <summary>
/// The whole payload uploaded to the remote endpoint. Serialized with
/// <c>JsonSerializerDefaults.Web</c>, so every property lands as camelCase.
/// </summary>
public sealed record RemoteSnapshot(
    int Version,
    DateTimeOffset GeneratedAt,
    string? Primary,
    IReadOnlyList<RemoteAccountSnapshot> Accounts,
    string DisplayMode);

/// <summary>
/// Builds the remote-view payload. Deliberately free of clocks and I/O: the
/// timestamp is passed in so the output is deterministic in tests.
/// </summary>
public static class RemoteSnapshotComposer
{
    /// <summary>Payload schema version; bump when the JSON contract changes.</summary>
    public const int SchemaVersion = 2;

    public const string SessionLabel = "Session";
    public const string WeeklyLabel = "Weekly";
    public const string UsedDisplayMode = "used";
    public const string RemainingDisplayMode = "remaining";

    public static RemoteSnapshot Compose(
        string? primaryProviderId,
        IEnumerable<RemoteSnapshotEntry> entries,
        DateTimeOffset generatedAt,
        bool showRemainingPercentages = false)
    {
        var accounts = entries.Select(ToAccount).ToList();

        return new RemoteSnapshot(
            SchemaVersion,
            generatedAt.ToUniversalTime(),
            string.IsNullOrWhiteSpace(primaryProviderId) ? null : primaryProviderId,
            accounts,
            showRemainingPercentages ? RemainingDisplayMode : UsedDisplayMode);
    }

    private static RemoteAccountSnapshot ToAccount(RemoteSnapshotEntry entry)
    {
        var windows = new List<RemoteWindowSnapshot>();

        if (entry.Usage is { } usage)
        {
            if (usage.SessionUsed is { } sessionUsed)
            {
                windows.Add(new RemoteWindowSnapshot(
                    SessionLabel,
                    ClampPercent(sessionUsed),
                    usage.SessionWindow?.ResetsAt,
                    Severity: SeverityText(usage.SessionSeverity)));
            }

            if (usage.WeekUsed is { } weekUsed)
            {
                windows.Add(new RemoteWindowSnapshot(
                    WeeklyLabel,
                    ClampPercent(weekUsed),
                    usage.WeekWindow?.ResetsAt,
                    Severity: SeverityText(usage.WeekSeverity)));
            }

            // A scoped window keeps the same Session/Weekly label as the
            // account-wide ones and carries the model name separately, so the
            // viewer can show "Weekly · Fable" instead of a bare "Fable".
            foreach (var quota in usage.ScopedQuotas)
            {
                windows.Add(new RemoteWindowSnapshot(
                    WindowLabelFor(quota.Group),
                    ClampPercent(quota.UsedPercent),
                    quota.ResetsAt,
                    quota.Label,
                    SeverityText(quota.Severity)));
            }
        }

        return new RemoteAccountSnapshot(
            entry.ProviderId,
            ExtractProvider(entry.ProviderId),
            entry.DisplayName,
            entry.Plan,
            windows,
            entry.Usage?.IsBlocked ?? false,
            ResetCreditsOf(entry.Usage));
    }

    /// <summary>
    /// Only publish reset credits there is something to redeem: an account with
    /// zero available leaves the key out of the payload altogether.
    /// </summary>
    private static RemoteResetCredits? ResetCreditsOf(UsagePulse? usage) =>
        usage is { ResetCreditsAvailable: > 0 } credited
            ? new RemoteResetCredits(credited.ResetCreditsAvailable, credited.ResetCreditExpiresAt)
            : null;

    private static string WindowLabelFor(string group) =>
        group.Contains("week", StringComparison.OrdinalIgnoreCase) ? WeeklyLabel : SessionLabel;

    private static string? SeverityText(QuotaSeverity? severity) => severity switch
    {
        QuotaSeverity.Normal => "normal",
        QuotaSeverity.Warning => "warning",
        QuotaSeverity.Critical => "critical",
        _ => null
    };

    /// <summary>
    /// Providers report percentages that can drift slightly outside 0-100
    /// (rounding, over-quota accounts); the viewer draws bars, so clamp.
    /// </summary>
    private static long ClampPercent(long value) => Math.Clamp(value, 0, 100);

    /// <summary>
    /// "claude:claude-1" -&gt; "claude"; bare ids such as "zai" are returned as-is.
    /// </summary>
    private static string ExtractProvider(string providerId)
    {
        var separator = providerId.IndexOf(':');
        return separator > 0 ? providerId[..separator] : providerId;
    }
}
