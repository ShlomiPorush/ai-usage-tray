using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Core.Tests.Providers;

public sealed class ClaudeOAuthUsageFetcherTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, true, true)]
    public void Session_refresh_runs_for_expiry_or_an_opted_in_token_near_expiry(
        bool keepSessionActive,
        bool tokenExpired,
        bool tokenExpiresSoon,
        bool expected)
    {
        Assert.Equal(
            expected,
            ClaudeOAuthUsageFetcher.ShouldRefreshSession(
                keepSessionActive,
                tokenExpired,
                tokenExpiresSoon));
    }
}
