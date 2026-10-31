using costats.Core.Antigravity;
using costats.Core.Pulse;
using Xunit;

namespace costats.Core.Tests.Pulse;

public sealed class RestoredUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_window_that_has_reset_is_dropped_while_the_others_stay()
    {
        var pulse = new UsagePulse(
            "codex:a",
            Now.AddHours(-10),
            SessionUsed: 60,
            SessionLimit: 100,
            WeekUsed: 30,
            WeekLimit: 100,
            SessionWindow: new QuotaWindow(TimeSpan.FromHours(5), Now.AddHours(-5)),
            WeekWindow: new QuotaWindow(TimeSpan.FromDays(7), Now.AddDays(3)));

        var kept = RestoredUsage.KeepCurrent(pulse, Now.AddHours(-10), Now)!;

        Assert.Null(kept.SessionUsed);
        Assert.Null(kept.SessionWindow);
        Assert.Equal(30, kept.WeekUsed);
    }

    [Fact]
    public void A_window_without_a_reset_time_is_kept_for_a_day_only()
    {
        var pulse = new UsagePulse("zai", Now, null, null, 40, 100, null, new QuotaWindow(TimeSpan.FromDays(7), null));

        Assert.NotNull(RestoredUsage.KeepCurrent(pulse, Now.AddHours(-23), Now));
        Assert.Null(RestoredUsage.KeepCurrent(pulse, Now.AddHours(-25), Now));
    }

    [Fact]
    public void A_monthly_window_is_kept_however_old_while_its_reset_is_ahead()
    {
        var pulse = new UsagePulse("copilot", Now, null, null, 40, 100, null, new QuotaWindow(TimeSpan.FromDays(30), Now.AddDays(20)));

        Assert.NotNull(RestoredUsage.KeepCurrent(pulse, Now.AddDays(-9), Now));
    }

    [Fact]
    public void Reset_credits_and_the_blocked_flag_never_come_back_from_disk()
    {
        var pulse = new UsagePulse("codex:a", Now, null, null, 40, 100, null, new QuotaWindow(TimeSpan.FromDays(7), Now.AddDays(2)))
        {
            IsBlocked = true,
            ResetCreditsAvailable = 2,
            ResetCreditExpiresAt = Now.AddDays(1)
        };

        var kept = RestoredUsage.KeepCurrent(pulse, Now.AddMinutes(-5), Now)!;

        Assert.False(kept.IsBlocked);
        Assert.Equal(0, kept.ResetCreditsAvailable);
        Assert.Null(kept.ResetCreditExpiresAt);
    }

    [Fact]
    public void A_record_stamped_in_the_future_is_not_trusted()
    {
        var pulse = new UsagePulse("zai", Now, null, null, 40, 100, null, new QuotaWindow(TimeSpan.FromDays(7), Now.AddDays(2)));

        Assert.Null(RestoredUsage.KeepCurrent(pulse, Now.AddMinutes(5), Now));
    }

    [Fact]
    public void The_stored_antigravity_quota_round_trips_without_any_email_and_expires_per_bucket()
    {
        var quota = new AgyQuotaReading(
            "Google AI Plus",
            new AgyQuotaBucket("gemini", "weekly", 100, Now.AddDays(4)),
            new AgyQuotaBucket("gemini", "5h", 20, Now.AddHours(-1)),
            [new ScopedQuota("Claude & GPT", "Weekly", 0, null, false)]);

        var text = AgyStoredQuota.Serialize(quota, Now.AddMinutes(-10));
        var read = AgyStoredQuota.Parse(text, Now)!;

        Assert.DoesNotContain("@", text, StringComparison.Ordinal);
        Assert.Equal(Now.AddMinutes(-10), read.CompletedAt);
        Assert.Equal(100, read.Quota.GeminiWeekly!.UsedPercent);
        Assert.Null(read.Quota.GeminiFiveHour);
        Assert.Single(read.Quota.OtherQuotas);
        Assert.Equal("Google AI Plus", read.Quota.PlanTier);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"version\":2,\"completedAt\":\"2026-10-31T19:00:00Z\"}")]
    [InlineData("{\"version\":1,\"completedAt\":\"2026-10-31T21:00:00Z\",\"geminiWeekly\":{\"family\":\"gemini\",\"window\":\"weekly\",\"usedPercent\":5,\"resetsAt\":\"2026-11-04T00:00:00Z\"}}")]
    [InlineData("{\"version\":1,\"completedAt\":\"2026-10-31T19:00:00Z\",\"geminiWeekly\":{\"family\":\"gemini\",\"window\":\"weekly\",\"usedPercent\":500,\"resetsAt\":\"2026-11-04T00:00:00Z\"}}")]
    public void A_stored_antigravity_quota_that_is_unknown_corrupt_future_or_out_of_range_is_ignored(string text)
    {
        Assert.Null(AgyStoredQuota.Parse(text, Now));
    }

    [Fact]
    public void The_usage_snapshot_keeps_usage_and_plan_but_no_identity_or_reset_credits()
    {
        var reading = new ProviderReading(
            new UsagePulse("claude:a", Now, 10, 100, 40, 100,
                new QuotaWindow(TimeSpan.FromHours(5), Now.AddHours(2)),
                new QuotaWindow(TimeSpan.FromDays(7), Now.AddDays(3)))
            {
                ResetCreditsAvailable = 1,
                ScopedQuotas = [new ScopedQuota("Fable", "Weekly", 7, Now.AddDays(3), true)]
            },
            new IdentityCard("claude:a", "Personal", "person@example.com", "Org Ltd", "Max 20x", "oauth"),
            "ok",
            Now,
            ReadingConfidence.High,
            ReadingSource.Api);

        var text = new UsageSnapshot(Now, [UsageSnapshot.EntryFor("claude:a", reading)!]).Serialize();
        var read = UsageSnapshot.Parse(text, Now)!;

        Assert.DoesNotContain("person@example.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Org Ltd", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Personal", text, StringComparison.Ordinal);
        var entry = Assert.Single(read.Entries);
        Assert.Equal("Max 20x", entry.Plan);
        Assert.Equal(40, entry.Usage.WeekUsed);
        Assert.Equal(0, entry.Usage.ResetCreditsAvailable);
        Assert.Equal("Fable", Assert.Single(entry.Usage.ScopedQuotas).Label);
        Assert.Equal(TimeSpan.FromDays(7), entry.Usage.WeekWindow!.Duration);
    }
}
