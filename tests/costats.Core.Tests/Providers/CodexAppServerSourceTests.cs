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
