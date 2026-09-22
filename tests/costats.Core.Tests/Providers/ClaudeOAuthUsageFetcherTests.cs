using System.Text.Json;
using costats.Core.Pulse;
using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Core.Tests.Providers;

public sealed class ClaudeOAuthUsageFetcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static (long Available, IReadOnlyList<ResetCredit>? Credits, DateTimeOffset? ExpiresAt) ParseGrants(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ClaudeOAuthUsageFetcher.ParseResetGrants(document.RootElement, Now);
    }

    [Fact]
    public void A_live_shaped_reset_grant_is_read_with_its_uses_window_and_covered_limits()
    {
        var (available, credits, expiresAt) = ParseGrants("""
        {
          "cedar_ember": {
            "eligible": true,
            "grants": [{
              "id": "opus55-launch-promax-20260921",
              "label": "Launch: one usage-limit reset",
              "resets_total": 1,
              "resets_left": 1,
              "starts_at": "2026-09-22T19:00:00+03:00",
              "ends_at": "2026-10-22T19:00:00+03:00",
              "clears": ["five_hour", "seven_day", "seven_day_overage_included"],
              "paused": false,
              "usable_now": true
            }]
          }
        }
        """);

        var credit = Assert.Single(credits!);
        Assert.Equal(1, available);
        Assert.Equal("opus55-launch-promax-20260921", credit.Id);
        Assert.Equal(ResetCredit.ClaudeType, credit.ResetType);
        Assert.Equal("Launch: one usage-limit reset", credit.Title);
        Assert.Equal("Clears the session limit, weekly limit, model weekly limit.", credit.Description);
        Assert.Equal(1, credit.UsesLeft);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 19, 0, 0, TimeSpan.FromHours(3)), credit.ExpiresAt);
        Assert.Equal(credit.ExpiresAt, expiresAt);
        Assert.True(credit.CanUseAt(Now));
        Assert.True(new ResetCreditBank(available, credits).IsComplete);
    }

    [Fact]
    public void A_multi_use_grant_counts_every_use_and_still_reads_complete()
    {
        var (available, credits, _) = ParseGrants("""
        {
          "cedar_ember": {
            "eligible": true,
            "grants": [
              { "id": "weekly", "resets_left": 2, "ends_at": "2026-10-01T00:00:00Z" },
              { "id": "promo", "resets_left": 1, "ends_at": "2026-09-25T00:00:00Z" }
            ]
          }
        }
        """);

        Assert.Equal(3, available);
        Assert.Equal(2, credits!.Count);
        Assert.True(new ResetCreditBank(available, credits).IsComplete);
    }

    [Fact]
    public void Paused_spent_expired_and_malformed_grants_are_not_offered()
    {
        var (available, credits, _) = ParseGrants("""
        {
          "cedar_ember": {
            "eligible": true,
            "grants": [
              { "id": "paused", "resets_left": 1, "paused": true },
              { "id": "spent", "resets_left": 0 },
              { "id": "expired", "resets_left": 1, "ends_at": "2026-09-01T00:00:00Z" },
              { "resets_left": 1 },
              "not-an-object"
            ]
          }
        }
        """);

        Assert.Equal(0, available);
        Assert.Null(credits);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "cedar_ember": null }""")]
    [InlineData("""{ "cedar_ember": { "eligible": false, "ineligible_reason": "cli_version", "grants": [] } }""")]
    [InlineData("""{ "cedar_ember": { "eligible": true, "grants": {} } }""")]
    public void An_absent_ineligible_or_malformed_block_reads_as_no_resets(string json)
    {
        var (available, credits, expiresAt) = ParseGrants(json);

        Assert.Equal(0, available);
        Assert.Null(credits);
        Assert.Null(expiresAt);
    }

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
