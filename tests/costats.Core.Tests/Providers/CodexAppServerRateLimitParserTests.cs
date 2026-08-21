using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Core.Tests.Providers;

public sealed class CodexAppServerRateLimitParserTests
{
    [Fact]
    public void Parse_converts_official_rate_limit_response_to_remaining_percentages()
    {
        const string json = """
        {"id":6,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":66,"windowDurationMins":300,"resetsAt":1785674040},"secondary":{"usedPercent":40,"windowDurationMins":10080,"resetsAt":1785945600},"planType":"prolite","rateLimitReachedType":null}}}
        """;

        var result = CodexAppServerRateLimitParser.Parse(json, expectedId: 6);

        Assert.NotNull(result);
        Assert.Equal(34, result.SessionRemainingPercent);
        Assert.Equal(TimeSpan.FromHours(5), result.SessionWindowDuration);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785674040), result.SessionResetsAt);
        Assert.Equal(60, result.WeeklyRemainingPercent);
        Assert.Equal(TimeSpan.FromDays(7), result.WeeklyWindowDuration);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785945600), result.WeeklyResetsAt);
        Assert.Equal("prolite", result.PlanType);
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

    [Fact]
    public void Parse_maps_per_model_limits_to_scoped_rows_and_skips_the_account_entry()
    {
        const string json = """
        {"id":6,"result":{"rateLimits":{
          "limitId":"codex",
          "primary":{"usedPercent":99,"windowDurationMins":10080,"resetsAt":1785945600},
          "secondary":null,
          "planType":"prolite",
          "rateLimitReachedType":null,
          "rateLimitsByLimitId":{
            "codex":{"limitId":"codex","primary":{"usedPercent":99,"windowDurationMins":10080,"resetsAt":1785945600},"secondary":null,"planType":"prolite","rateLimitReachedType":null},
            "codex_bengalfox":{"limitId":"codex_bengalfox","limitName":"GPT-5.3-Codex-Spark","primary":{"usedPercent":0,"windowDurationMins":300,"resetsAt":1785674040},"secondary":{"usedPercent":0,"windowDurationMins":10080,"resetsAt":1785945600},"planType":"prolite","rateLimitReachedType":null}
          }}}}
        """;

        var result = CodexAppServerRateLimitParser.Parse(json, expectedId: 6);

        Assert.NotNull(result);
        Assert.Equal(1, result.WeeklyRemainingPercent);
        Assert.Equal(2, result.ScopedQuotas.Count);

        var session = result.ScopedQuotas[0];
        Assert.Equal("GPT-5.3-Codex-Spark", session.Label);
        Assert.Equal("session", session.Group);
        Assert.Equal(0, session.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785674040), session.ResetsAt);
        Assert.False(session.IsActive);
        Assert.Null(session.Severity);

        var weekly = result.ScopedQuotas[1];
        Assert.Equal("GPT-5.3-Codex-Spark", weekly.Label);
        Assert.Equal("weekly", weekly.Group);
        Assert.Equal(0, weekly.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785945600), weekly.ResetsAt);

        Assert.False(result.IsBlocked);
    }

    [Fact]
    public void Parse_classifies_a_model_window_by_duration_not_by_position()
    {
        const string json = """
        {"id":6,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":10,"windowDurationMins":300},
          "rateLimitsByLimitId":{
            "codex_spark":{"limitId":"codex_spark","limitName":"Spark","primary":{"usedPercent":25,"windowDurationMins":10080},"secondary":null}
          }}}}
        """;

        var result = CodexAppServerRateLimitParser.Parse(json, expectedId: 6);

        var scoped = Assert.Single(result!.ScopedQuotas);
        Assert.Equal("weekly", scoped.Group);
        Assert.Equal(25, scoped.UsedPercent);
    }

    [Fact]
    public void Parse_falls_back_to_the_limit_id_when_a_model_has_no_name()
    {
        const string json = """
        {"id":6,"result":{"rateLimits":{"limitId":"codex",
          "rateLimitsByLimitId":{
            "codex_bengalfox":{"limitId":"codex_bengalfox","primary":{"usedPercent":4,"windowDurationMins":300},"secondary":null}
          }}}}
        """;

        var result = CodexAppServerRateLimitParser.Parse(json, expectedId: 6);

        Assert.Equal("codex_bengalfox", Assert.Single(result!.ScopedQuotas).Label);
    }

    [Fact]
    public void Parse_blocks_the_account_when_the_account_wide_entry_reports_a_reached_limit()
    {
        const string json = """
        {"id":6,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":100,"windowDurationMins":300},"rateLimitReachedType":"primary"}}}
        """;

        var result = CodexAppServerRateLimitParser.Parse(json, expectedId: 6);

        Assert.True(result!.IsBlocked);
    }

    [Fact]
    public void Parse_blocks_the_account_when_spend_control_is_reached()
    {
        const string json = """
        {"id":6,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":12,"windowDurationMins":300},"rateLimitReachedType":null,"spendControlReached":true}}}
        """;

        var result = CodexAppServerRateLimitParser.Parse(json, expectedId: 6);

        Assert.True(result!.IsBlocked);
    }

    [Fact]
    public void Parse_blocks_the_account_from_the_matching_entry_in_rate_limits_by_limit_id()
    {
        const string json = """
        {"id":6,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":100,"windowDurationMins":300},
          "rateLimitsByLimitId":{
            "codex":{"limitId":"codex","primary":{"usedPercent":100,"windowDurationMins":300},"spendControlReached":true}
          }}}}
        """;

        var result = CodexAppServerRateLimitParser.Parse(json, expectedId: 6);

        Assert.True(result!.IsBlocked);
        Assert.Empty(result.ScopedQuotas);
    }

    [Fact]
    public void Parse_does_not_block_the_account_when_only_a_model_reached_its_limit()
    {
        const string json = """
        {"id":6,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":30,"windowDurationMins":300},"rateLimitReachedType":null,
          "rateLimitsByLimitId":{
            "codex_bengalfox":{"limitId":"codex_bengalfox","limitName":"GPT-5.3-Codex-Spark","primary":{"usedPercent":100,"windowDurationMins":300},"secondary":null,"rateLimitReachedType":"primary"}
          }}}}
        """;

        var result = CodexAppServerRateLimitParser.Parse(json, expectedId: 6);

        Assert.False(result!.IsBlocked);
        var scoped = Assert.Single(result.ScopedQuotas);
        Assert.True(scoped.IsActive);
        Assert.Equal(100, scoped.UsedPercent);
    }

    [Fact]
    public void Parse_returns_no_scoped_rows_when_the_payload_has_no_per_model_limits()
    {
        const string json = """
        {"id":6,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":66,"windowDurationMins":300},"secondary":null}}}
        """;

        var result = CodexAppServerRateLimitParser.Parse(json, expectedId: 6);

        Assert.Empty(result!.ScopedQuotas);
        Assert.False(result.IsBlocked);
    }
}
