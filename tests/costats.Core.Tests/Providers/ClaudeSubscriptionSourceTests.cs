using costats.Core.Pulse;
using costats.Application.SessionActivation;
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
            DateTimeOffset.Parse("2026-08-02T14:00:00Z"),
            Email: "person@example.com"));

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
        Assert.Equal("person@example.com", reading.Identity.Email);
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
    public async Task ReadAsync_exposes_sign_in_required_without_stale_usage()
    {
        var source = new ClaudeSubscriptionSource(
            new ClaudeAccountProfile("claude-1", "Claude", "/tmp/claude"),
            new FakeClaudeSubscriptionUsageClient(
                null,
                ProviderAuthenticationState.SignInRequired));

        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.Null(reading.Usage);
        Assert.Equal(ProviderAuthenticationState.SignInRequired, reading.AuthenticationState);
        Assert.Contains("Sign-in required", reading.StatusSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_exposes_a_reset_confirmed_by_successful_activation_while_the_api_is_delayed()
    {
        var confirmedReset = DateTimeOffset.UtcNow.AddHours(5);
        var registry = new SessionActivationWindowRegistry();
        registry.Confirm("claude:claude-1", confirmedReset);
        var client = new FakeClaudeSubscriptionUsageClient(new ClaudeOAuthUsageResult(
            0,
            null,
            18,
            DateTimeOffset.UtcNow.AddDays(4),
            false,
            null,
            null,
            "pro",
            null,
            DateTimeOffset.UtcNow));
        var source = new ClaudeSubscriptionSource(
            new ClaudeAccountProfile("claude-1", "Claude", "/tmp/claude"),
            client,
            registry);

        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(0, reading.Usage!.SessionUsed);
        Assert.Equal(confirmedReset, reading.Usage.SessionWindow!.ResetsAt);
    }

    [Fact]
    public async Task ReadAsync_includes_the_max_multiplier_from_the_rate_limit_tier()
    {
        var client = new FakeClaudeSubscriptionUsageClient(new ClaudeOAuthUsageResult(
            10, null, 20, null, false, null, null,
            "max",
            "default_claude_max_20x",
            DateTimeOffset.Parse("2026-08-20T09:00:00Z")));

        var source = new ClaudeSubscriptionSource(new ClaudeAccountProfile("claude-1", "Claude", "/tmp/claude"), client);
        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.Equal("Max 20x", reading.Identity!.Plan);
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

    [Fact]
    public async Task ReadAsync_carries_the_severity_Claude_reports_for_each_window()
    {
        var client = new FakeClaudeSubscriptionUsageClient(new ClaudeOAuthUsageResult(
            2,
            DateTimeOffset.Parse("2026-08-21T15:10:00Z"),
            89,
            DateTimeOffset.Parse("2026-08-21T12:00:00Z"),
            false,
            null,
            null,
            "max",
            "default_claude_max_20x",
            DateTimeOffset.Parse("2026-08-21T11:00:00Z"),
            [new ScopedQuota("Fable", "weekly", 100, null, true) { Severity = QuotaSeverity.Critical }],
            QuotaSeverity.Normal,
            QuotaSeverity.Warning));

        var source = new ClaudeSubscriptionSource(new ClaudeAccountProfile("claude-1", "Claude", "/tmp/claude"), client);
        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(QuotaSeverity.Normal, reading.Usage!.SessionSeverity);
        Assert.Equal(QuotaSeverity.Warning, reading.Usage.WeekSeverity);
        Assert.Equal(QuotaSeverity.Critical, Assert.Single(reading.Usage.ScopedQuotas).Severity);
        Assert.False(reading.Usage.IsBlocked);
    }

    private sealed class FakeClaudeSubscriptionUsageClient(
        ClaudeOAuthUsageResult? result,
        ProviderAuthenticationState authenticationState = ProviderAuthenticationState.Unknown)
        : IClaudeSubscriptionUsageClient
    {
        public ProviderAuthenticationState AuthenticationState => authenticationState;

        public Task<ClaudeOAuthUsageResult?> FetchAsync(CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }
}
