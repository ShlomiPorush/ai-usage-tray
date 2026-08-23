using System.Net;
using System.Text;
using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Core.Tests.Providers;

public sealed class ZaiUsageFetcherTests
{
    [Fact]
    public async Task FetchAsync_uses_the_official_quota_endpoint_and_raw_authorization_value()
    {
        var handler = new RecordingHandler("""
        {
          "code": 200,
          "msg": "success",
          "success": true,
          "data": {
            "level": "lite",
            "limits": [
              { "type": "CREDIT_LIMIT", "unit": 3, "number": 5, "percentage": 0 }
            ]
          }
        }
        """);
        using var fetcher = new ZaiUsageFetcher(handler);

        var snapshot = await fetcher.FetchAsync("test-key", null, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("https://api.z.ai/api/monitor/usage/quota/limit", handler.RequestUri?.AbsoluteUri);
        Assert.Equal("test-key", handler.Authorization);
    }

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Authorization = request.Headers.TryGetValues("Authorization", out var values)
                ? values.SingleOrDefault()
                : null;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
