using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Core.Tests.Providers;

public sealed class ClaudeOAuthProfileParserTests
{
    [Fact]
    public void ParseEmail_reads_and_trims_the_account_email()
    {
        const string json = """
        {
          "account": {
            "email": " person@example.com ",
            "display_name": "Person",
            "has_claude_max": true
          },
          "organization": {
            "organization_type": "claude_max"
          }
        }
        """;

        Assert.Equal("person@example.com", ClaudeOAuthProfileParser.ParseEmail(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"account\":null}")]
    [InlineData("{\"account\":{\"email\":null}}")]
    public void ParseEmail_returns_null_when_the_email_is_unavailable(string json)
    {
        Assert.Null(ClaudeOAuthProfileParser.ParseEmail(json));
    }
}
