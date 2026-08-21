using costats.Core.Pulse;
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
            60, TimeSpan.FromDays(7), resetsAt.AddDays(3)));
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

    private sealed class StubClient(CodexAppServerRateLimitSnapshot snapshot) : ICodexAppServerClient
    {
        public string? LastCodexHome { get; private set; }

        public Task<CodexAppServerRateLimitSnapshot?> FetchAsync(string codexHome, CancellationToken cancellationToken)
        {
            LastCodexHome = codexHome;
            return Task.FromResult<CodexAppServerRateLimitSnapshot?>(snapshot);
        }
    }
}
