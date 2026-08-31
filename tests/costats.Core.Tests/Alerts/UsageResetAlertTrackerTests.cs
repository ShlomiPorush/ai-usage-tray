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

        Assert.Empty(tracker.Observe(State(0), ["codex:personal"]));
    }

    [Fact]
    public void Non_zero_to_zero_emits_once_and_rearms_after_usage_returns()
    {
        var tracker = new UsageResetAlertTracker();
        tracker.Observe(State(42), ["codex:personal"]);

        var alert = Assert.Single(tracker.Observe(State(0), ["codex:personal"]));
        Assert.Equal("codex:personal", alert.ProviderId);
        Assert.Equal("session", alert.WindowKey);
        Assert.Empty(tracker.Observe(State(0), ["codex:personal"]));

        tracker.Observe(State(3), ["codex:personal"]);
        Assert.Single(tracker.Observe(State(0), ["codex:personal"]));
    }

    [Fact]
    public void Disabled_account_does_not_alert_when_enabled_later()
    {
        var tracker = new UsageResetAlertTracker();
        tracker.Observe(State(42), []);
        tracker.Observe(State(0), []);

        Assert.Empty(tracker.Observe(State(0), ["codex:personal"]));
    }

    [Fact]
    public void Checkpoints_restore_the_previous_reading()
    {
        var first = new UsageResetAlertTracker();
        first.Observe(State(42), ["codex:personal"]);

        var restored = new UsageResetAlertTracker(first.ExportCheckpoints());
        Assert.Single(restored.Observe(State(0), ["codex:personal"]));
    }

    private static PulseState State(long session) =>
        new(
            new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex:personal"] = new(
                    new UsagePulse(
                        "codex:personal",
                        Now,
                        session,
                        100,
                        null,
                        null,
                        null,
                        null),
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
