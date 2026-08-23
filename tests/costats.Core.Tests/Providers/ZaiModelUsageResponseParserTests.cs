using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Core.Tests.Providers;

public sealed class ZaiModelUsageResponseParserTests
{
    [Fact]
    public void Parse_reads_daily_totals_calls_and_per_model_series()
    {
        const string body = """
        {
          "code": 200,
          "data": {
            "x_time": ["2026-08-21", "2026-08-22", "2026-08-23"],
            "modelCallCount": [2, 3, 4],
            "tokensUsage": [100, 250, 650],
            "modelDataList": [
              {
                "modelName": "GLM-5.3",
                "tokensUsage": [100, 200, 500],
                "totalTokens": 800
              },
              {
                "modelName": "GLM-4.7",
                "tokensUsage": [0, 50, 150],
                "totalTokens": 200
              }
            ]
          }
        }
        """;

        var result = ZaiModelUsageResponseParser.Parse(body);

        Assert.NotNull(result);
        Assert.Equal([new DateOnly(2026, 8, 21), new DateOnly(2026, 8, 22), new DateOnly(2026, 8, 23)], result!.Days);
        Assert.Equal([100L, 250L, 650L], result.TokensByDay);
        Assert.Equal(9, result.TotalCalls);
        Assert.Equal(1000, result.TotalTokens);
        Assert.Equal(["GLM-5.3", "GLM-4.7"], result.Models.Select(model => model.ModelName));
        Assert.Equal([100L, 200L, 500L], result.Models[0].TokensByDay);
    }

    [Fact]
    public void Parse_returns_null_for_an_unrecognized_response()
    {
        Assert.Null(ZaiModelUsageResponseParser.Parse("{}"));
        Assert.Null(ZaiModelUsageResponseParser.Parse("{not-json"));
    }
}
