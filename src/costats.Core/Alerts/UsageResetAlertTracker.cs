using costats.Core.Pulse;

namespace costats.Core.Alerts;

/// <summary>A weekly quota window that changed from non-zero used percent to zero.</summary>
public sealed record UsageResetAlert(
    string ProviderId,
    string WindowKey,
    string WindowLabel,
    string? Scope,
    DateTimeOffset? ResetsAt);

/// <summary>The persisted last reading for one account quota window.</summary>
public sealed record UsageResetCheckpoint(
    string ProviderId,
    string WindowKey,
    long UsedPercent,
    bool Enabled,
    DateTimeOffset? ResetsAt);

/// <summary>
/// Detects explicit weekly usage resets. A reset is deliberately narrow: the
/// same account and weekly window must move from &gt;0% used to exactly 0% used
/// while its alert is enabled. Readings are retained when alerts are disabled
/// so turning the feature on never creates a retroactive notification.
/// </summary>
public sealed class UsageResetAlertTracker
{
    private readonly Dictionary<WindowIdentity, WindowState> states = [];

    public UsageResetAlertTracker(IEnumerable<UsageResetCheckpoint>? checkpoints = null)
    {
        if (checkpoints is null)
        {
            return;
        }

        foreach (var checkpoint in checkpoints)
        {
            if (string.IsNullOrWhiteSpace(checkpoint.ProviderId) ||
                string.IsNullOrWhiteSpace(checkpoint.WindowKey) ||
                !IsWeeklyKey(checkpoint.WindowKey))
            {
                continue;
            }

            states[new WindowIdentity(checkpoint.ProviderId.Trim(), checkpoint.WindowKey.Trim())] =
                new WindowState(
                    Math.Clamp(checkpoint.UsedPercent, 0, 100),
                    checkpoint.Enabled,
                    checkpoint.ResetsAt);
        }
    }

    public IReadOnlyList<UsageResetAlert> Observe(
        PulseState pulse,
        IEnumerable<string> enabledProviderIds)
    {
        ArgumentNullException.ThrowIfNull(pulse);
        ArgumentNullException.ThrowIfNull(enabledProviderIds);

        var enabled = enabledProviderIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var alerts = new List<UsageResetAlert>();

        foreach (var (providerId, reading) in pulse.Providers)
        {
            if (reading.Usage is not { } usage)
            {
                continue;
            }

            var providerEnabled = enabled.Contains(providerId);
            foreach (var window in WindowsOf(usage))
            {
                var identity = new WindowIdentity(providerId, window.Key);
                if (states.TryGetValue(identity, out var previous) &&
                    providerEnabled &&
                    previous.Enabled &&
                    previous.UsedPercent > 0 &&
                    window.UsedPercent == 0)
                {
                    alerts.Add(new UsageResetAlert(
                        providerId,
                        window.Key,
                        window.Label,
                        window.Scope,
                        window.ResetsAt));
                }

                states[identity] = new WindowState(
                    window.UsedPercent,
                    providerEnabled,
                    window.ResetsAt);
            }
        }

        return alerts;
    }

    public IReadOnlyList<UsageResetCheckpoint> ExportCheckpoints() =>
        states.Select(pair => new UsageResetCheckpoint(
                pair.Key.ProviderId,
                pair.Key.WindowKey,
                pair.Value.UsedPercent,
                pair.Value.Enabled,
                pair.Value.ResetsAt))
            .ToArray();

    private static IEnumerable<WindowReading> WindowsOf(UsagePulse usage)
    {
        if (usage.WeekUsed is { } weekUsed)
        {
            yield return new WindowReading(
                "weekly",
                "Weekly",
                null,
                ClampPercent(weekUsed),
                usage.WeekWindow?.ResetsAt);
        }

        foreach (var quota in usage.ScopedQuotas)
        {
            if (!quota.Group.Contains("week", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var scope = quota.Label.Trim();
            yield return new WindowReading(
                $"scoped:weekly:{scope.ToLowerInvariant()}",
                "Weekly",
                scope,
                ClampPercent(quota.UsedPercent),
                quota.ResetsAt);
        }
    }

    private static bool IsWeeklyKey(string key) =>
        key.Equals("weekly", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("scoped:weekly:", StringComparison.OrdinalIgnoreCase);

    private static long ClampPercent(long usedPercent) => Math.Clamp(usedPercent, 0, 100);

    private sealed record WindowReading(
        string Key,
        string Label,
        string? Scope,
        long UsedPercent,
        DateTimeOffset? ResetsAt);

    private sealed record WindowState(long UsedPercent, bool Enabled, DateTimeOffset? ResetsAt);

    private sealed record WindowIdentity(string ProviderId, string WindowKey)
    {
        public bool Equals(WindowIdentity? other) =>
            other is not null &&
            string.Equals(ProviderId, other.ProviderId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(WindowKey, other.WindowKey, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(ProviderId),
            StringComparer.OrdinalIgnoreCase.GetHashCode(WindowKey));
    }
}
