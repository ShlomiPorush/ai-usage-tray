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

    [Fact]
    public void Accounts_that_share_a_directory_name_do_not_share_a_cache_file()
    {
        // Every Claude profile directory is called ".claude", so the last path
        // segment alone cannot tell two accounts apart.
        var alice = ProfileDirectory("alice");
        var bob = ProfileDirectory("bob");

        Assert.NotEqual(
            ClaudeOAuthUsageFetcher.BuildDiskCacheFileName(alice),
            ClaudeOAuthUsageFetcher.BuildDiskCacheFileName(bob));
    }

    [Fact]
    public void A_configured_account_never_shares_the_cache_file_of_an_unconfigured_one()
    {
        var withoutConfigDir = ClaudeOAuthUsageFetcher.BuildDiskCacheFileName(null);

        Assert.NotEqual(withoutConfigDir, ClaudeOAuthUsageFetcher.BuildDiskCacheFileName(ProfileDirectory("alice")));
        Assert.NotEqual(
            withoutConfigDir,
            ClaudeOAuthUsageFetcher.BuildDiskCacheFileName(ProfileDirectory("alice") + Path.DirectorySeparatorChar));
        Assert.Equal(withoutConfigDir, ClaudeOAuthUsageFetcher.BuildDiskCacheFileName("   "));
    }

    [Fact]
    public void One_directory_written_in_different_ways_keeps_one_cache_file()
    {
        var alice = ProfileDirectory("alice");
        var expected = ClaudeOAuthUsageFetcher.BuildDiskCacheFileName(alice);

        Assert.Equal(expected, ClaudeOAuthUsageFetcher.BuildDiskCacheFileName(alice + Path.DirectorySeparatorChar));
        Assert.Equal(
            expected,
            ClaudeOAuthUsageFetcher.BuildDiskCacheFileName(Path.Combine(alice, "sub", "..")));

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(expected, ClaudeOAuthUsageFetcher.BuildDiskCacheFileName(alice.ToUpperInvariant()));
            Assert.Equal(expected, ClaudeOAuthUsageFetcher.BuildDiskCacheFileName(alice.ToLowerInvariant()));
        }
    }

    [Fact]
    public void A_cache_file_name_stays_readable_and_safe()
    {
        var name = ClaudeOAuthUsageFetcher.BuildDiskCacheFileName(ProfileDirectory("alice"));

        Assert.StartsWith("claude-oauth_claude_", name);
        Assert.EndsWith(".json", name);
        Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    private static string ProfileDirectory(string owner) =>
        Path.Combine(Path.GetTempPath(), "costats-cache-key", owner, ".claude");
}
