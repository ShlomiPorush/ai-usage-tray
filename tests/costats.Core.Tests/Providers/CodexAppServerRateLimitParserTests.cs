using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Core.Tests.Providers;

public sealed class CodexAppServerRateLimitParserTests
{
    [Fact]
    public void Parse_converts_official_rate_limit_response_to_remaining_percentages()
    {
        const string json = """
        {"id":6,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":66,"windowDurationMins":300,"resetsAt":1785674040},"secondary":{"usedPercent":40,"windowDurationMins":10080,"resetsAt":1785945600},"rateLimitReachedType":null}}}
        """;

        var result = CodexAppServerRateLimitParser.Parse(json, expectedId: 6);

        Assert.NotNull(result);
        Assert.Equal(34, result.SessionRemainingPercent);
        Assert.Equal(TimeSpan.FromHours(5), result.SessionWindowDuration);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785674040), result.SessionResetsAt);
        Assert.Equal(60, result.WeeklyRemainingPercent);
        Assert.Equal(TimeSpan.FromDays(7), result.WeeklyWindowDuration);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785945600), result.WeeklyResetsAt);
    }

    [Fact]
    public void Parse_classifies_a_single_seven_day_primary_window_as_weekly()
    {
        const string json = """
        {"id":6,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":13,"windowDurationMins":10080,"resetsAt":1786287600},"secondary":null}}}
        """;

        var result = CodexAppServerRateLimitParser.Parse(json, expectedId: 6);

        Assert.NotNull(result);
        Assert.Null(result.SessionRemainingPercent);
        Assert.Null(result.SessionWindowDuration);
        Assert.Equal(87, result.WeeklyRemainingPercent);
        Assert.Equal(TimeSpan.FromDays(7), result.WeeklyWindowDuration);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1786287600), result.WeeklyResetsAt);
    }

    [Fact]
    public void Parse_ignores_notifications_and_responses_for_other_request_ids()
    {
        Assert.Null(CodexAppServerRateLimitParser.Parse(
            "{\"method\":\"account/rateLimits/updated\",\"params\":{}}", expectedId: 6));
        Assert.Null(CodexAppServerRateLimitParser.Parse(
            "{\"id\":5,\"result\":{}}", expectedId: 6));
    }

    [Fact]
    public void Parse_returns_null_for_json_rpc_error()
    {
        var result = CodexAppServerRateLimitParser.Parse(
            "{\"id\":6,\"error\":{\"code\":-32000,\"message\":\"Not authenticated\"}}",
            expectedId: 6);

        Assert.Null(result);
    }
}
