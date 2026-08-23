using costats.Core.RemoteView;
using Xunit;

namespace costats.Core.Tests.RemoteView;

public sealed class RemoteViewEndpointsTests
{
    [Theory]
    [InlineData("https://ai.yaaps.net")]
    [InlineData("https://ai.yaaps.net/")]
    [InlineData("https://usage-api.example.com:8443/base")]
    [InlineData("HTTPS://ai.yaaps.net")]
    public void Https_urls_are_allowed(string url)
    {
        Assert.True(RemoteViewEndpoints.IsAllowed(url));
    }

    [Theory]
    [InlineData("http://localhost:8787")]
    [InlineData("http://127.0.0.1:8787")]
    [InlineData("http://[::1]:8787")]
    public void Plain_http_is_allowed_on_loopback_for_local_testing(string url)
    {
        Assert.True(RemoteViewEndpoints.IsAllowed(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://ai.yaaps.net")]
    [InlineData("http://192.168.1.10:8787")]
    [InlineData("ftp://ai.yaaps.net")]
    [InlineData("file:///C:/tmp")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ai.yaaps.net")]
    [InlineData("/u/abc")]
    [InlineData("not a url at all")]
    public void Everything_else_is_rejected(string? url)
    {
        Assert.False(RemoteViewEndpoints.IsAllowed(url));
        Assert.Null(RemoteViewEndpoints.Normalize(url));
    }

    [Fact]
    public void Normalize_trims_an_allowed_url_and_keeps_it_otherwise_untouched()
    {
        Assert.Equal("https://ai.yaaps.net", RemoteViewEndpoints.Normalize("  https://ai.yaaps.net  "));
    }
}
