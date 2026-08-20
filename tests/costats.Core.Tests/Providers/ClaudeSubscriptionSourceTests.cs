using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Core.Tests.Providers;

public sealed class ClaudeSubscriptionSourceTests
{
    [Fact]
    public async Task ReadAsync_exposes_account_wide_session_and_weekly_subscription_usage()
    {
        var sessionReset = DateTimeOffset.Parse("2026-08-02T18:00:00Z");
        var weeklyReset = DateTimeOffset.Parse("2026-08-08T18:00:00Z");
        var client = new FakeClaudeSubscriptionUsageClient(new ClaudeOAuthUsageResult(
            23.4,
            sessionReset,
            41.2,
            weeklyReset,
            false,
            null,
            null,
            "pro",
            null,
            DateTimeOffset.Parse("2026-08-02T14:00:00Z")));

        var source = new ClaudeSubscriptionSource(new ClaudeAccountProfile("claude-1", "Claude", "/tmp/claude"), client);
        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.NotNull(reading.Usage);
        Assert.Equal("claude:claude-1", reading.Usage!.ProviderId);
        Assert.Equal(23, reading.Usage.SessionUsed);
        Assert.Equal(100, reading.Usage.SessionLimit);
        Assert.Equal(41, reading.Usage.WeekUsed);
        Assert.Equal(100, reading.Usage.WeekLimit);
        Assert.Equal(sessionReset, reading.Usage.SessionWindow!.ResetsAt);
        Assert.Equal(weeklyReset, reading.Usage.WeekWindow!.ResetsAt);
        Assert.Equal("Pro", reading.Identity!.Plan);
    }

    [Fact]
    public async Task ReadAsync_does_not_invent_usage_when_subscription_profile_is_not_connected()
    {
        var source = new ClaudeSubscriptionSource(
            new ClaudeAccountProfile("claude-1", "Claude", "/tmp/claude"),
            new FakeClaudeSubscriptionUsageClient(null));

        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.Null(reading.Usage);
        Assert.Equal("Claude: Claude subscription is not connected", reading.StatusSummary);
    }

    [Fact]
    public async Task ReadAsync_exposes_model_scoped_limits_such_as_fable()
    {
        var reset = DateTimeOffset.Parse("2026-08-21T12:00:00Z");
        var client = new FakeClaudeSubscriptionUsageClient(new ClaudeOAuthUsageResult(
            7,
            null,
            88,
            null,
            false,
            null,
            null,
            "max",
            null,
            DateTimeOffset.Parse("2026-08-20T09:00:00Z"),
            [new costats.Core.Pulse.ScopedQuota("Fable", "weekly", 100, reset, true)]));

        var source = new ClaudeSubscriptionSource(new ClaudeAccountProfile("claude-1", "Claude", "/tmp/claude"), client);
        var reading = await source.ReadAsync(CancellationToken.None);

        var scoped = Assert.Single(reading.Usage!.ScopedQuotas);
        Assert.Equal("Fable", scoped.Label);
        Assert.Equal(100, scoped.UsedPercent);
        Assert.Equal(reset, scoped.ResetsAt);
        Assert.True(scoped.IsActive);
    }

    private sealed class FakeClaudeSubscriptionUsageClient(ClaudeOAuthUsageResult? result)
        : IClaudeSubscriptionUsageClient
    {
        public Task<ClaudeOAuthUsageResult?> FetchAsync(CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }
}
