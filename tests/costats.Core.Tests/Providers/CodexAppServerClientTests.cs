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
    public void Account_read_request_can_use_another_id()
    {
        Assert.Equal(
            "{\"method\":\"account/read\",\"id\":4,\"params\":{\"refreshToken\":false}}",
            CodexAppServerClient.CreateAccountReadRequest(refreshToken: false, id: 4));
    }

    [Fact]
    public async Task Failed_session_refresh_does_not_report_a_signed_in_account_as_signed_out()
    {
        // Seen live: a refreshing account/read answered "no account" and left
        // that app-server treating the account as signed out, while a plain
        // read showed it signed in with rate limits.
        var fake = await FakeCodex.CreateAsync(signedIn: true, refreshFails: true);
        try
        {
            var result = await fake.FetchAsync(refreshToken: true);

            Assert.NotNull(result);
            Assert.False(result.RequiresSignIn);
            Assert.Equal(34, result.SessionRemainingPercent);
            Assert.Equal("person@example.com", result.Email);
            Assert.Contains("refresh", fake.ReceivedRequests(), StringComparison.Ordinal);
        }
        finally
        {
            fake.Dispose();
        }
    }

    [Fact]
    public async Task Signed_out_account_requires_sign_in_with_or_without_a_refresh()
    {
        foreach (var refreshToken in new[] { false, true })
        {
            var fake = await FakeCodex.CreateAsync(signedIn: false, refreshFails: true);
            try
            {
                var result = await fake.FetchAsync(refreshToken);

                Assert.NotNull(result);
                Assert.True(result.RequiresSignIn);
            }
            finally
            {
                fake.Dispose();
            }
        }
    }

    [Fact]
    public async Task No_session_refresh_is_requested_unless_asked()
    {
        var fake = await FakeCodex.CreateAsync(signedIn: true, refreshFails: false);
        try
        {
            var result = await fake.FetchAsync(refreshToken: false);

            Assert.NotNull(result);
            Assert.DoesNotContain("refresh", fake.ReceivedRequests(), StringComparison.Ordinal);
        }
        finally
        {
            fake.Dispose();
        }
    }

    /// <summary>
    /// A fake app-server that answers each request as it arrives. A refreshing
    /// account/read can be made to fail, after which this process reports the
    /// account as signed out, as the real one does.
    /// </summary>
    private sealed class FakeCodex : IDisposable
    {
        private readonly DirectoryInfo _directory;

        private FakeCodex(DirectoryInfo directory) => _directory = directory;

        private string Executable => Path.Combine(_directory.FullName, "fake-codex.cmd");
        private string RequestLog => Path.Combine(_directory.FullName, "requests.log");

        public static async Task<FakeCodex> CreateAsync(bool signedIn, bool refreshFails)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("This fake is a Windows batch file.");
            }

            var fake = new FakeCodex(Directory.CreateTempSubdirectory("fake-codex-"));
            var script = Path.Combine(fake._directory.FullName, "fake-codex.ps1");
            await File.WriteAllTextAsync(script, """
                $signedIn = $__SIGNED_IN__
                $refreshFails = $__REFRESH_FAILS__
                $log = '__REQUEST_LOG__'
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                  if ($line -match '"refreshToken":true') { Add-Content $log 'refresh'; if ($refreshFails) { $signedIn = $false } }
                  if ($line -notmatch '"id":(\d+)') { continue }
                  $id = $Matches[1]
                  if ($line -match 'account/rateLimits/read') {
                    [Console]::Out.WriteLine('{"id":' + $id + ',"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":66,"windowDurationMins":300,"resetsAt":1785674040},"secondary":{"usedPercent":40,"windowDurationMins":10080,"resetsAt":1785945600}}}}')
                  } elseif ($line -match 'account/read') {
                    if ($signedIn) { [Console]::Out.WriteLine('{"id":' + $id + ',"result":{"account":{"type":"chatgpt","email":"person@example.com","planType":"pro"},"requiresOpenaiAuth":false}}') }
                    else { [Console]::Out.WriteLine('{"id":' + $id + ',"result":{"account":null,"requiresOpenaiAuth":true}}') }
                  }
                  [Console]::Out.Flush()
                }
                """
                .Replace("__SIGNED_IN__", signedIn ? "true" : "false", StringComparison.Ordinal)
                .Replace("__REFRESH_FAILS__", refreshFails ? "true" : "false", StringComparison.Ordinal)
                .Replace("__REQUEST_LOG__", fake.RequestLog, StringComparison.Ordinal));
            await File.WriteAllTextAsync(fake.Executable,
                $"@powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"\r\n");
            return fake;
        }

        public async Task<CodexAppServerRateLimitSnapshot?> FetchAsync(bool refreshToken)
        {
            using var client = new CodexAppServerClient(Executable, TimeSpan.FromSeconds(20));
            return await client.FetchAsync(
                Path.Combine(_directory.FullName, "home"),
                refreshToken,
                CancellationToken.None);
        }

        public string ReceivedRequests() =>
            File.Exists(RequestLog) ? File.ReadAllText(RequestLog) : string.Empty;

        public void Dispose()
        {
            try
            {
                _directory.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The fake may still be exiting; the temp folder is harmless.
            }
        }
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
