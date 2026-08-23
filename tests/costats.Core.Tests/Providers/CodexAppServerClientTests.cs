using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Core.Tests.Providers;

public sealed class CodexAppServerClientTests
{
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
            read limits
            read account
            printf '%s\n' '{"id":3,"result":{"account":{"type":"chatgpt","email":"person@example.com","planType":"plus"},"requiresOpenaiAuth":false}}'
            printf '%s\n' '{"id":2,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":66,"windowDurationMins":300,"resetsAt":1785674040},"secondary":{"usedPercent":40,"windowDurationMins":10080,"resetsAt":1785945600}}}}'
            """);
        File.SetUnixFileMode(fakeCodex,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            using var client = new CodexAppServerClient(fakeCodex, TimeSpan.FromSeconds(5));

            var result = await client.FetchAsync(tempDirectory.FullName, CancellationToken.None);

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
