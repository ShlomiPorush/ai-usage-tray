using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Core.Tests.Providers;

public sealed class ClaudeOAuthProfileParserTests
{
    [Fact]
    public void Parse_reads_email_and_current_plan()
    {
        const string json = """
        {
          "account": {
            "email": " person@example.com ",
            "display_name": "Person",
            "has_claude_max": true
          },
          "organization": {
            "organization_type": "claude_max",
            "rate_limit_tier": "default_claude_max_20x"
          }
        }
        """;

        var profile = ClaudeOAuthProfileParser.Parse(json);

        Assert.NotNull(profile);
        Assert.Equal("person@example.com", profile.Email);
        Assert.Equal("max", profile.SubscriptionType);
        Assert.Equal("default_claude_max_20x", profile.RateLimitTier);
    }

    [Fact]
    public void Parse_keeps_email_when_the_organization_is_missing()
    {
        const string json = """{"account":{"email":"person@example.com"}}""";

        var profile = ClaudeOAuthProfileParser.Parse(json);

        Assert.NotNull(profile);
        Assert.Equal("person@example.com", profile.Email);
        Assert.Null(profile.SubscriptionType);
        Assert.Null(profile.RateLimitTier);
    }

    [Fact]
    public void Parse_keeps_plan_when_the_email_is_missing()
    {
        const string json = """
        {"organization":{"organization_type":"claude_pro","rate_limit_tier":"default_claude_pro"}}
        """;

        var profile = ClaudeOAuthProfileParser.Parse(json);

        Assert.NotNull(profile);
        Assert.Null(profile.Email);
        Assert.Equal("pro", profile.SubscriptionType);
        Assert.Equal("default_claude_pro", profile.RateLimitTier);
    }

    [Fact]
    public void Parse_passes_unprefixed_organization_types_through()
    {
        const string json = """{"organization":{"organization_type":"enterprise"}}""";

        Assert.Equal("enterprise", ClaudeOAuthProfileParser.Parse(json)?.SubscriptionType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"account\":null}")]
    [InlineData("{\"account\":{\"email\":null}}")]
    [InlineData("{\"account\":{\"email\":\" \"},\"organization\":{\"organization_type\":\"\"}}")]
    public void Parse_returns_null_when_nothing_usable_is_present(string json)
    {
        Assert.Null(ClaudeOAuthProfileParser.Parse(json));
    }
}
