using costats.Core.Alerts;
using costats.Core.Pulse;
using Xunit;

namespace costats.Core.Tests.Alerts;

public sealed class UsageAlertTrackerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void First_reading_establishes_a_baseline_without_alerting()
    {
        var tracker = new UsageAlertTracker();

        var alerts = tracker.Observe(State(session: 82, weekly: 91), [Rule(80)]);

        Assert.Empty(alerts);
    }

    [Fact]
    public void Windows_cross_and_rearm_independently()
    {
        var tracker = new UsageAlertTracker();
        tracker.Observe(State(session: 79, weekly: 82), [Rule(80)]);

        var sessionCrossing = Assert.Single(tracker.Observe(State(session: 81, weekly: 83), [Rule(80)]));
        Assert.Equal("session", sessionCrossing.WindowKey);
        Assert.Equal(81, sessionCrossing.UsedPercent);

        Assert.Empty(tracker.Observe(State(session: 90, weekly: 84), [Rule(80)]));
        Assert.Empty(tracker.Observe(State(session: 4, weekly: 85), [Rule(80)]));

        var nextSessionCrossing = Assert.Single(
            tracker.Observe(State(session: 80, weekly: 86), [Rule(80)]));
        Assert.Equal("session", nextSessionCrossing.WindowKey);
    }

    [Fact]
    public void Accounts_use_their_own_thresholds()
    {
        var tracker = new UsageAlertTracker();
        tracker.Observe(
            State(("claude:work", 79L), ("codex:personal", 94L)),
            [new UsageAlertRule("claude:work", 80), new UsageAlertRule("codex:personal", 95)]);

        var alerts = tracker.Observe(
            State(("claude:work", 80L), ("codex:personal", 95L)),
            [new UsageAlertRule("claude:work", 80), new UsageAlertRule("codex:personal", 95)]);

        Assert.Equal(2, alerts.Count);
        Assert.Contains(alerts, alert => alert.ProviderId == "claude:work" && alert.ThresholdPercent == 80);
        Assert.Contains(alerts, alert => alert.ProviderId == "codex:personal" && alert.ThresholdPercent == 95);
    }

    [Fact]
    public void Disabled_accounts_are_removed_and_reenabled_accounts_start_with_a_new_baseline()
    {
        var tracker = new UsageAlertTracker();
        tracker.Observe(State(session: 79), [Rule(80)]);
        tracker.Observe(State(session: 81), []);

        Assert.Empty(tracker.Observe(State(session: 82), [Rule(80)]));
    }

    [Fact]
    public void Scoped_windows_with_the_same_group_are_tracked_by_scope()
    {
        var tracker = new UsageAlertTracker();
        tracker.Observe(State(scoped: [("Fable", 79), ("Sonnet", 91)]), [Rule(80)]);

        var alert = Assert.Single(
            tracker.Observe(State(scoped: [("Fable", 81), ("Sonnet", 92)]), [Rule(80)]));

        Assert.Equal("Fable", alert.Scope);
        Assert.Equal("scoped:weekly:fable", alert.WindowKey);
    }

    [Fact]
    public void A_new_reset_cycle_can_alert_when_the_first_observed_value_is_already_above_threshold()
    {
        var tracker = new UsageAlertTracker();
        tracker.Observe(State(session: 98, sessionReset: Now.AddHours(1)), [Rule(80)]);

        var alert = Assert.Single(
            tracker.Observe(State(session: 82, sessionReset: Now.AddHours(6)), [Rule(80)]));

        Assert.Equal("session", alert.WindowKey);
    }

    private static UsageAlertRule Rule(int threshold) => new("claude:work", threshold);

    private static PulseState State(
        long? session = null,
        long? weekly = null,
        DateTimeOffset? sessionReset = null,
        IReadOnlyList<(string Scope, long Used)>? scoped = null) =>
        State(("claude:work", session ?? 0), weekly, sessionReset, scoped, includeSession: session.HasValue);

    private static PulseState State(
        (string ProviderId, long Session) first,
        (string ProviderId, long Session) second) =>
        new(
            new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase)
            {
                [first.ProviderId] = Reading(first.ProviderId, first.Session),
                [second.ProviderId] = Reading(second.ProviderId, second.Session)
            },
            Now,
            [],
            false,
            RefreshTrigger.Scheduled);

    private static PulseState State(
        (string ProviderId, long Session) account,
        long? weekly,
        DateTimeOffset? sessionReset,
        IReadOnlyList<(string Scope, long Used)>? scoped,
        bool includeSession) =>
        new(
            new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase)
            {
                [account.ProviderId] = Reading(
                    account.ProviderId,
                    includeSession ? account.Session : null,
                    weekly,
                    sessionReset,
                    scoped)
            },
            Now,
            [],
            false,
            RefreshTrigger.Scheduled);

    private static ProviderReading Reading(
        string providerId,
        long? session,
        long? weekly = null,
        DateTimeOffset? sessionReset = null,
        IReadOnlyList<(string Scope, long Used)>? scoped = null) =>
        new(
            new UsagePulse(
                providerId,
                Now,
                session,
                session.HasValue ? 100 : null,
                weekly,
                weekly.HasValue ? 100 : null,
                sessionReset is null ? null : new QuotaWindow(TimeSpan.FromHours(5), sessionReset),
                null)
            {
                ScopedQuotas = (scoped ?? [])
                    .Select(item => new ScopedQuota(item.Scope, "weekly", item.Used, Now.AddDays(2), true))
                    .ToArray()
            },
            null,
            null,
            Now,
            ReadingConfidence.High,
            ReadingSource.Api);
}
