using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Core.Tests.Providers;

public sealed class ZaiResponseParserTests
{
    [Fact]
    public void Parse_returns_null_for_empty_or_whitespace_body()
    {
        Assert.Null(ZaiResponseParser.Parse(string.Empty));
        Assert.Null(ZaiResponseParser.Parse("   "));
    }

    [Fact]
    public void Parse_returns_null_on_invalid_json()
    {
        Assert.Null(ZaiResponseParser.Parse("{not-json"));
    }

    [Fact]
    public void Parse_returns_null_when_response_is_an_empty_object()
    {
        Assert.Null(ZaiResponseParser.Parse("{}"));
    }

    [Fact]
    public void Parse_reads_wrapped_envelope_with_session_and_weekly_remaining()
    {
        const string body = """
        {
          "code": 200,
          "msg": "ok",
          "data": {
            "plan": "GLM Coding Plan",
            "five_hour": { "remaining": 73, "reset_at": "2026-08-09T18:00:00Z" },
            "weekly":   { "remaining": 41, "reset_at": "2026-08-15T18:00:00Z" }
          }
        }
        """;

        var snapshot = ZaiResponseParser.Parse(body);

        Assert.NotNull(snapshot);
        Assert.Equal(73, snapshot!.SessionRemainingPercent);
        Assert.Equal(41, snapshot.WeeklyRemainingPercent);
        Assert.Equal("GLM Coding Plan", snapshot.PlanName);
        Assert.Equal(DateTimeOffset.Parse("2026-08-09T18:00:00Z").ToUniversalTime(),
            snapshot.SessionResetsAt?.ToUniversalTime());
        Assert.Equal(DateTimeOffset.Parse("2026-08-15T18:00:00Z").ToUniversalTime(),
            snapshot.WeeklyResetsAt?.ToUniversalTime());
    }

    [Fact]
    public void Parse_reads_flat_envelope_without_data_wrapper()
    {
        const string body = """
        {
          "plan": "Standard",
          "fiveHour": { "remaining_percent": 50, "resets_at": "2026-08-09T18:00:00Z" },
          "sevenDay": { "limit_remaining": 10, "next_reset": "2026-08-15T18:00:00Z" }
        }
        """;

        var snapshot = ZaiResponseParser.Parse(body);

        Assert.NotNull(snapshot);
        Assert.Equal(50, snapshot!.SessionRemainingPercent);
        Assert.Equal(10, snapshot.WeeklyRemainingPercent);
        Assert.Equal("Standard", snapshot.PlanName);
    }

    [Fact]
    public void Parse_computes_remaining_from_used_over_total_when_only_used_is_returned()
    {
        const string body = """
        {
          "data": {
            "five_hour": { "used": 50, "total": 200 },
            "weekly":   { "used_tokens": 1000, "limit": 5000 }
          }
        }
        """;

        var snapshot = ZaiResponseParser.Parse(body);

        Assert.NotNull(snapshot);
        Assert.Equal(75, snapshot!.SessionRemainingPercent);  // 100 - (50/200*100)
        Assert.Equal(80, snapshot.WeeklyRemainingPercent);    // 100 - (1000/5000*100)
    }

    [Fact]
    public void Parse_handles_partial_response_with_only_session_window()
    {
        const string body = """
        { "data": { "five_hour": { "remaining": 5 } } }
        """;

        var snapshot = ZaiResponseParser.Parse(body);

        Assert.NotNull(snapshot);
        Assert.Equal(5, snapshot!.SessionRemainingPercent);
        Assert.Null(snapshot.WeeklyRemainingPercent);
        Assert.Null(snapshot.WeeklyResetsAt);
    }

    [Fact]
    public void Parse_handles_unix_timestamp_reset()
    {
        const string body = """
        { "data": { "five_hour": { "remaining": 95, "reset_at": 1754152800 } } }
        """;

        var snapshot = ZaiResponseParser.Parse(body);

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot!.SessionResetsAt);
    }

    [Fact]
    public void Parse_falls_back_to_flat_remaining_when_no_window_breakdown_is_present()
    {
        const string body = """
        { "data": { "plan": "Pay-as-you-go", "remaining": 63 } }
        """;

        var snapshot = ZaiResponseParser.Parse(body);

        Assert.NotNull(snapshot);
        Assert.Equal(63, snapshot!.WeeklyRemainingPercent);
        Assert.Equal("Pay-as-you-go", snapshot.PlanName);
    }

    [Fact]
    public void Parse_reads_window_duration_when_provided_in_seconds()
    {
        const string body = """
        { "data": { "five_hour": { "remaining": 50, "window_seconds": 18000 } } }
        """;

        var snapshot = ZaiResponseParser.Parse(body);

        Assert.NotNull(snapshot);
        Assert.Equal(TimeSpan.FromSeconds(18000), snapshot!.SessionWindow);
    }

    [Fact]
    public void Parse_reads_current_credit_limit_response_with_hourly_and_weekly_windows()
    {
        const string body = """
        {
          "code": 200,
          "msg": "success",
          "success": true,
          "data": {
            "level": "lite",
            "limits": [
              {
                "type": "CREDIT_LIMIT",
                "unit": 3,
                "number": 5,
                "usage": 2000,
                "currentValue": 0,
                "remaining": 2000,
                "percentage": 0
              },
              {
                "type": "CREDIT_LIMIT",
                "unit": 6,
                "number": 1,
                "usage": 10000,
                "currentValue": 112,
                "remaining": 9887,
                "percentage": 1,
                "nextResetTime": 1787927085998
              }
            ]
          }
        }
        """;

        var snapshot = ZaiResponseParser.Parse(body);

        Assert.NotNull(snapshot);
        Assert.Equal(100, snapshot!.SessionRemainingPercent);
        Assert.Equal(99, snapshot.WeeklyRemainingPercent);
        Assert.Equal(TimeSpan.FromHours(5), snapshot.SessionWindow);
        Assert.Equal(TimeSpan.FromDays(7), snapshot.WeeklyWindow);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1787927085998), snapshot.WeeklyResetsAt);
        Assert.Equal("lite", snapshot.PlanName);
    }

    [Fact]
    public void Parse_derives_remaining_percentage_when_credit_response_omits_percentage()
    {
        const string body = """
        {
          "data": {
            "limits": [
              { "type": "CREDIT_LIMIT", "unit": 3, "number": 5, "usage": 2000, "remaining": 1500 }
            ]
          }
        }
        """;

        var snapshot = ZaiResponseParser.Parse(body);

        Assert.NotNull(snapshot);
        Assert.Equal(75, snapshot!.SessionRemainingPercent);
    }
}
