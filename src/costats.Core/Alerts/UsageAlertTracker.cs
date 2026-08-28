using costats.Core.Pulse;

namespace costats.Core.Alerts;

/// <summary>An enabled used-percent threshold for one provider account.</summary>
public sealed record UsageAlertRule(string ProviderId, int ThresholdPercent);

/// <summary>A quota window that crossed its account's configured threshold.</summary>
public sealed record UsageThresholdAlert(
    string ProviderId,
    string WindowKey,
    string WindowLabel,
    string? Scope,
    long UsedPercent,
    int ThresholdPercent,
    DateTimeOffset? ResetsAt);

/// <summary>
/// Tracks threshold state independently for every account and quota window.
/// The first reading establishes a baseline. Later readings emit once on a
/// crossing and rearm only after the window falls below its threshold or a
/// new reset cycle is observed.
/// </summary>
public sealed class UsageAlertTracker
{
    private readonly Dictionary<WindowIdentity, WindowState> states = [];

    public IReadOnlyList<UsageThresholdAlert> Observe(
        PulseState pulse,
        IEnumerable<UsageAlertRule> rules)
    {
        ArgumentNullException.ThrowIfNull(pulse);
        ArgumentNullException.ThrowIfNull(rules);

        var activeRules = rules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.ProviderId))
            .GroupBy(rule => rule.ProviderId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToDictionary(
                rule => rule.ProviderId.Trim(),
                rule => Math.Clamp(rule.ThresholdPercent, 1, 100),
                StringComparer.OrdinalIgnoreCase);
        var observed = new HashSet<WindowIdentity>();
        var alerts = new List<UsageThresholdAlert>();

        foreach (var (providerId, threshold) in activeRules)
        {
            if (!pulse.Providers.TryGetValue(providerId, out var reading) || reading.Usage is not { } usage)
            {
                continue;
            }

            foreach (var window in WindowsOf(usage))
            {
                var identity = new WindowIdentity(providerId, window.Key);
                observed.Add(identity);

                if (!states.TryGetValue(identity, out var previous))
                {
                    states[identity] = new WindowState(window.UsedPercent, window.ResetsAt);
                    continue;
                }

                var crossed = previous.UsedPercent < threshold && window.UsedPercent >= threshold;
                var startedNewCycleAboveThreshold =
                    previous.ResetsAt is { } oldReset &&
                    window.ResetsAt is { } newReset &&
                    newReset > oldReset &&
                    window.UsedPercent < previous.UsedPercent &&
                    window.UsedPercent >= threshold;

                if (crossed || startedNewCycleAboveThreshold)
                {
                    alerts.Add(new UsageThresholdAlert(
                        providerId,
                        window.Key,
                        window.Label,
                        window.Scope,
                        window.UsedPercent,
                        threshold,
                        window.ResetsAt));
                }

                states[identity] = new WindowState(window.UsedPercent, window.ResetsAt);
            }
        }

        foreach (var stale in states.Keys.Where(key => !observed.Contains(key)).ToArray())
        {
            states.Remove(stale);
        }

        return alerts;
    }

    private static IEnumerable<WindowReading> WindowsOf(UsagePulse usage)
    {
        if (usage.SessionUsed is { } sessionUsed)
        {
            yield return new WindowReading(
                "session",
                "Session",
                null,
                ClampPercent(sessionUsed),
                usage.SessionWindow?.ResetsAt);
        }

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
            var group = quota.Group.Contains("week", StringComparison.OrdinalIgnoreCase)
                ? "weekly"
                : "session";
            var scope = quota.Label.Trim();
            yield return new WindowReading(
                $"scoped:{group}:{scope.ToLowerInvariant()}",
                group == "weekly" ? "Weekly" : "Session",
                scope,
                ClampPercent(quota.UsedPercent),
                quota.ResetsAt);
        }
    }

    private static long ClampPercent(long usedPercent) => Math.Clamp(usedPercent, 0, 100);

    private sealed record WindowReading(
        string Key,
        string Label,
        string? Scope,
        long UsedPercent,
        DateTimeOffset? ResetsAt);

    private sealed record WindowState(long UsedPercent, DateTimeOffset? ResetsAt);

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
