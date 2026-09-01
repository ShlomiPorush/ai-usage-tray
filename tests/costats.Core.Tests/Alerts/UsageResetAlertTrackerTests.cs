using costats.Core.Alerts;
using costats.Core.Pulse;
using Xunit;

namespace costats.Core.Tests.Alerts;

public sealed class UsageResetAlertTrackerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void First_zero_reading_establishes_a_baseline_without_alerting()
    {
        var tracker = new UsageResetAlertTracker();

        Assert.Empty(tracker.Observe(State(weekly: 0), ["codex:personal"]));
    }

    [Fact]
    public void Non_zero_to_zero_emits_once_and_rearms_after_usage_returns()
    {
        var tracker = new UsageResetAlertTracker();
        tracker.Observe(State(weekly: 42), ["codex:personal"]);

        var alert = Assert.Single(tracker.Observe(State(weekly: 0), ["codex:personal"]));
        Assert.Equal("codex:personal", alert.ProviderId);
        Assert.Equal("weekly", alert.WindowKey);
        Assert.Empty(tracker.Observe(State(weekly: 0), ["codex:personal"]));

        tracker.Observe(State(weekly: 3), ["codex:personal"]);
        Assert.Single(tracker.Observe(State(weekly: 0), ["codex:personal"]));
    }

    [Fact]
    public void Session_reset_does_not_alert()
    {
        var tracker = new UsageResetAlertTracker();
        tracker.Observe(State(session: 42, weekly: 42), ["codex:personal"]);

        Assert.Empty(tracker.Observe(State(session: 0, weekly: 42), ["codex:personal"]));
    }

    [Fact]
    public void Disabled_account_does_not_alert_when_enabled_later()
    {
        var tracker = new UsageResetAlertTracker();
        tracker.Observe(State(weekly: 42), []);
        tracker.Observe(State(weekly: 0), []);

        Assert.Empty(tracker.Observe(State(weekly: 0), ["codex:personal"]));
    }

    [Fact]
    public void Scoped_weekly_reset_collapses_into_the_account_wide_alert()
    {
        var tracker = new UsageResetAlertTracker();
        tracker.Observe(State(weekly: 42, scopedWeekly: 17), ["codex:personal"]);

        var alert = Assert.Single(tracker.Observe(State(weekly: 0, scopedWeekly: 0), ["codex:personal"]));
        Assert.Equal("weekly", alert.WindowKey);
        Assert.Null(alert.Scope);
    }

    [Fact]
    public void Scoped_weekly_reset_alone_still_alerts()
    {
        var tracker = new UsageResetAlertTracker();
        tracker.Observe(State(weekly: 0, scopedWeekly: 17), ["codex:personal"]);

        var alert = Assert.Single(tracker.Observe(State(weekly: 0, scopedWeekly: 0), ["codex:personal"]));
        Assert.Equal("scoped:weekly:fable", alert.WindowKey);
        Assert.Equal("Fable", alert.Scope);
    }

    [Fact]
    public void Checkpoints_restore_the_previous_reading()
    {
        var first = new UsageResetAlertTracker();
        first.Observe(State(weekly: 42), ["codex:personal"]);

        var restored = new UsageResetAlertTracker(first.ExportCheckpoints());
        Assert.Single(restored.Observe(State(weekly: 0), ["codex:personal"]));
    }

    [Fact]
    public void Legacy_session_checkpoint_is_discarded()
    {
        var restored = new UsageResetAlertTracker(
        [
            new UsageResetCheckpoint("codex:personal", "session", 42, true, null)
        ]);

        Assert.Empty(restored.ExportCheckpoints());
        Assert.Empty(restored.Observe(State(session: 0, weekly: 42), ["codex:personal"]));
    }

    private static PulseState State(long? session = null, long? weekly = null, long? scopedWeekly = null) =>
        new(
            new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex:personal"] = new(
                    new UsagePulse(
                        "codex:personal",
                        Now,
                        session,
                        session.HasValue ? 100 : null,
                        weekly,
                        weekly.HasValue ? 100 : null,
                        null,
                        null)
                    {
                        ScopedQuotas = scopedWeekly.HasValue
                            ? [new ScopedQuota("Fable", "week", scopedWeekly.Value, null, true)]
                            : []
                    },
                    null,
                    null,
                    Now,
                    ReadingConfidence.High,
                    ReadingSource.Api)
            },
            Now,
            [],
            false,
            RefreshTrigger.Scheduled);
}
