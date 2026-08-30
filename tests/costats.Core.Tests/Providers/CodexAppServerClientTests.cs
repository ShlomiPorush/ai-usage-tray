using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Core.Tests.Providers;

public sealed class CodexAppServerClientTests
{
    [Theory]
    [InlineData(false, "{\"method\":\"account/read\",\"id\":3,\"params\":{\"refreshToken\":false}}")]
    [InlineData(true, "{\"method\":\"account/read\",\"id\":3,\"params\":{\"refreshToken\":true}}")]
    public void Account_read_request_follows_the_session_refresh_opt_in(
        bool refreshToken,
        string expected)
    {
        Assert.Equal(expected, CodexAppServerClient.CreateAccountReadRequest(refreshToken));
    }

    [Theory]
    [InlineData("401 Unauthorized")]
    [InlineData("refresh token expired")]
    [InlineData("authentication required")]
    [InlineData("Please sign in again")]
    public void Authentication_errors_are_recognized_for_alerting(string error)
    {
        Assert.True(CodexAppServerClient.IsAuthenticationError(error));
    }

    [Fact]
    public void Ordinary_app_server_errors_do_not_claim_that_sign_in_expired()
    {
        Assert.False(CodexAppServerClient.IsAuthenticationError("Rate limit payload was unavailable"));
    }

    [Fact]
    public async Task FetchAsync_sends_handshake_and_returns_rate_limits()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("fake-codex-");
        var fakeCodex = Path.Combine(tempDirectory.FullName, "codex");
        await File.WriteAllTextAsync(fakeCodex, """
            #!/bin/sh
            read initialize
            read initialized
            read account
            printf '%s\n' '{"id":3,"result":{"account":{"type":"chatgpt","email":"person@example.com","planType":"plus"},"requiresOpenaiAuth":false}}'
            read limits
            printf '%s\n' '{"id":2,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":66,"windowDurationMins":300,"resetsAt":1785674040},"secondary":{"usedPercent":40,"windowDurationMins":10080,"resetsAt":1785945600}}}}'
            """);
        File.SetUnixFileMode(fakeCodex,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            using var client = new CodexAppServerClient(fakeCodex, TimeSpan.FromSeconds(5));

            var result = await client.FetchAsync(
                tempDirectory.FullName,
                refreshToken: true,
                CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(34, result.SessionRemainingPercent);
            Assert.Equal(60, result.WeeklyRemainingPercent);
            Assert.Equal("person@example.com", result.Email);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }
}
