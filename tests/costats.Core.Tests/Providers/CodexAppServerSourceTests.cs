using costats.Core.Pulse;
using costats.Application.SessionActivation;
using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Core.Tests.Providers;

public sealed class CodexAppServerSourceTests
{
    [Fact]
    public async Task ReadAsync_exposes_each_account_as_a_distinct_provider()
    {
        var resetsAt = new DateTimeOffset(2026, 8, 2, 14, 34, 0, TimeSpan.Zero);
        var client = new StubClient(new CodexAppServerRateLimitSnapshot(
            34, TimeSpan.FromHours(5), resetsAt,
            60, TimeSpan.FromDays(7), resetsAt.AddDays(3))
        {
            Email = "person@example.com"
        });
        var source = new CodexAppServerSource(
            new CodexAccountProfile("openai-1", "OpenAI 1", "C:/profiles/openai-1"), client);

        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.Equal("codex:openai-1", source.Profile.ProviderId);
        Assert.Equal("OpenAI 1", source.Profile.DisplayName);
        Assert.Equal(66, reading.Usage?.SessionUsed);
        Assert.Equal(100, reading.Usage?.SessionLimit);
        Assert.Equal(40, reading.Usage?.WeekUsed);
        Assert.Equal(100, reading.Usage?.WeekLimit);
        Assert.Equal(resetsAt, reading.Usage?.SessionWindow?.ResetsAt);
        Assert.Equal("person@example.com", reading.Identity?.Email);
        Assert.Equal("C:/profiles/openai-1", client.LastCodexHome);
    }

    [Fact]
    public async Task ReadAsync_publishes_scoped_model_quotas_and_the_blocked_flag()
    {
        var resetsAt = new DateTimeOffset(2026, 8, 2, 14, 34, 0, TimeSpan.Zero);
        var snapshot = new CodexAppServerRateLimitSnapshot(
            1, TimeSpan.FromDays(7), resetsAt,
            null, null, null,
            "prolite")
        {
            ScopedQuotas =
            [
                new ScopedQuota("GPT-5.3-Codex-Spark", "session", 0, resetsAt, false),
                new ScopedQuota("GPT-5.3-Codex-Spark", "weekly", 0, resetsAt, false)
            ],
            IsBlocked = true
        };
        var source = new CodexAppServerSource(
            new CodexAccountProfile("openai-1", "OpenAI 1", "C:/profiles/openai-1"),
            new StubClient(snapshot));

        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.True(reading.Usage!.IsBlocked);
        Assert.Equal(2, reading.Usage.ScopedQuotas.Count);
        Assert.All(reading.Usage.ScopedQuotas, quota =>
        {
            Assert.Equal("GPT-5.3-Codex-Spark", quota.Label);
            Assert.Equal(0, quota.UsedPercent);
            Assert.Null(quota.Severity);
        });
        Assert.Equal(["session", "weekly"], reading.Usage.ScopedQuotas.Select(quota => quota.Group));
    }

    [Fact]
    public async Task ReadAsync_leaves_the_blocked_flag_clear_when_the_account_is_not_refused()
    {
        var source = new CodexAppServerSource(
            new CodexAccountProfile("openai-1", "OpenAI 1", "C:/profiles/openai-1"),
            new StubClient(new CodexAppServerRateLimitSnapshot(
                34, TimeSpan.FromHours(5), DateTimeOffset.UtcNow,
                60, TimeSpan.FromDays(7), DateTimeOffset.UtcNow.AddDays(3))));

        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.False(reading.Usage!.IsBlocked);
        Assert.Empty(reading.Usage.ScopedQuotas);
    }

    [Fact]
    public async Task ReadAsync_never_exposes_a_zero_used_idle_reset()
    {
        var rollingReset = DateTimeOffset.UtcNow.AddHours(5);
        var source = new CodexAppServerSource(
            new CodexAccountProfile("openai-1", "GPT", "C:/profiles/openai-1"),
            new StubClient(new CodexAppServerRateLimitSnapshot(
                100, TimeSpan.FromHours(5), rollingReset,
                100, TimeSpan.FromDays(7), rollingReset.AddDays(7))));

        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(0, reading.Usage!.SessionUsed);
        Assert.Null(reading.Usage.SessionWindow!.ResetsAt);
        Assert.Equal(TimeSpan.FromHours(5), reading.Usage.SessionWindow.Duration);
        Assert.Null(reading.Usage.WeekWindow!.ResetsAt);

        var repeated = await source.ReadAsync(CancellationToken.None);
        Assert.Null(repeated.Usage!.SessionWindow!.ResetsAt);
        Assert.Null(repeated.Usage.WeekWindow!.ResetsAt);
    }

    [Fact]
    public async Task ReadAsync_exposes_a_zero_used_reset_confirmed_by_successful_activation()
    {
        var resetAt = DateTimeOffset.UtcNow.AddHours(5);
        var registry = new SessionActivationWindowRegistry();
        registry.Confirm("codex:openai-1", resetAt);
        var source = new CodexAppServerSource(
            new CodexAccountProfile("openai-1", "GPT", "C:/profiles/openai-1"),
            new StubClient(new CodexAppServerRateLimitSnapshot(
                100, TimeSpan.FromHours(5), resetAt,
                100, TimeSpan.FromDays(7), resetAt.AddDays(7))),
            registry);

        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(0, reading.Usage!.SessionUsed);
        Assert.Equal(resetAt, reading.Usage.SessionWindow!.ResetsAt);
        Assert.Null(reading.Usage.WeekWindow!.ResetsAt);
    }

    [Fact]
    public async Task ReadAsync_keeps_a_moving_zero_used_reset_idle()
    {
        var firstReset = DateTimeOffset.UtcNow.AddHours(5);
        var source = new CodexAppServerSource(
            new CodexAccountProfile("openai-1", "GPT", "C:/profiles/openai-1"),
            new SequenceStubClient(
                new CodexAppServerRateLimitSnapshot(
                    100, TimeSpan.FromHours(5), firstReset,
                    100, TimeSpan.FromDays(7), firstReset.AddDays(7)),
                new CodexAppServerRateLimitSnapshot(
                    100, TimeSpan.FromHours(5), firstReset.AddMinutes(5),
                    100, TimeSpan.FromDays(7), firstReset.AddDays(7).AddMinutes(5))));

        var first = await source.ReadAsync(CancellationToken.None);
        var second = await source.ReadAsync(CancellationToken.None);

        Assert.Null(first.Usage!.SessionWindow!.ResetsAt);
        Assert.Null(second.Usage!.SessionWindow!.ResetsAt);
        Assert.Null(second.Usage.WeekWindow!.ResetsAt);
    }

    [Fact]
    public async Task ReadAsync_forwards_reset_credits_to_the_pulse()
    {
        var expiresAt = new DateTimeOffset(2026, 9, 20, 8, 21, 24, TimeSpan.Zero);
        var snapshot = new CodexAppServerRateLimitSnapshot(
            null, null, null,
            95, TimeSpan.FromDays(7), expiresAt.AddDays(-10),
            "prolite")
        {
            ResetCreditsAvailable = 1,
            ResetCreditExpiresAt = expiresAt
        };
        var source = new CodexAppServerSource(
            new CodexAccountProfile("openai-1", "OpenAI 1", "C:/profiles/openai-1"),
            new StubClient(snapshot));

        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(1, reading.Usage!.ResetCreditsAvailable);
        Assert.Equal(expiresAt, reading.Usage.ResetCreditExpiresAt);
    }

    [Fact]
    public async Task ReadAsync_reports_no_reset_credits_when_the_provider_sent_none()
    {
        var source = new CodexAppServerSource(
            new CodexAccountProfile("openai-1", "OpenAI 1", "C:/profiles/openai-1"),
            new StubClient(new CodexAppServerRateLimitSnapshot(
                34, TimeSpan.FromHours(5), DateTimeOffset.UtcNow,
                60, TimeSpan.FromDays(7), DateTimeOffset.UtcNow.AddDays(3))));

        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(0, reading.Usage!.ResetCreditsAvailable);
        Assert.Null(reading.Usage.ResetCreditExpiresAt);
    }

    private sealed class StubClient(CodexAppServerRateLimitSnapshot snapshot) : ICodexAppServerClient
    {
        public string? LastCodexHome { get; private set; }

        public Task<CodexAppServerRateLimitSnapshot?> FetchAsync(string codexHome, CancellationToken cancellationToken)
        {
            LastCodexHome = codexHome;
            return Task.FromResult<CodexAppServerRateLimitSnapshot?>(snapshot);
        }
    }

    private sealed class SequenceStubClient(params CodexAppServerRateLimitSnapshot[] snapshots)
        : ICodexAppServerClient
    {
        private int _index;

        public Task<CodexAppServerRateLimitSnapshot?> FetchAsync(
            string codexHome,
            CancellationToken cancellationToken)
        {
            var snapshot = snapshots[Math.Min(_index, snapshots.Length - 1)];
            _index++;
            return Task.FromResult<CodexAppServerRateLimitSnapshot?>(snapshot);
        }
    }
}
