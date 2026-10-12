using System.Globalization;
using costats.Application.Diagnostics;
using costats.Application.Storage;
using costats.Infrastructure.Providers;
using costats.Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Xunit;

namespace costats.Core.Tests.Diagnostics;

public sealed class CodexFailureLoggingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ai-usage-tray-codex-failure-tests", Guid.NewGuid().ToString("N"));

    public CodexFailureLoggingTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Temp cleanup is best effort.
        }
    }

    [Fact]
    public async Task A_failing_start_warns_once_and_recovery_is_logged_once()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var codexHome = Path.Combine(_root, "codex-1");
        var fakeCodex = Path.Combine(_root, "codex-later.cmd");
        var logger = new CapturingLogger<CodexAppServerClient>();
        using var client = new CodexAppServerClient(fakeCodex, TimeSpan.FromSeconds(10), logger);

        // Three refreshes while Codex cannot start.
        for (var i = 0; i < 3; i++)
        {
            await client.FetchAsync(codexHome, refreshToken: false, CancellationToken.None);
        }

        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(3, logger.Entries.Count(entry => entry.Level == LogLevel.Debug && entry.Text.Contains("failed (starting the app-server)", StringComparison.Ordinal)));

        // Codex gets installed; the next read answers.
        await File.WriteAllTextAsync(fakeCodex, string.Join("\r\n",
            "@echo off",
            "echo {\"id\":3,\"result\":{\"account\":{\"type\":\"chatgpt\"},\"requiresOpenaiAuth\":false}}",
            "echo {\"id\":2,\"result\":{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":10,\"windowDurationMins\":300,\"resetsAt\":1785674040}}}}",
            "set /p request=",
            "ping -n 6 127.0.0.1 >nul",
            ""));

        var result = await client.FetchAsync(codexHome, refreshToken: false, CancellationToken.None);
        await client.FetchAsync(codexHome, refreshToken: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information && entry.Text.Contains("works again", StringComparison.Ordinal));
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_different_failure_is_warned_about_again()
    {
        var codexHome = Path.Combine(_root, "codex-2");
        var logger = new CapturingLogger<CodexAppServerClient>();
        using var missing = new CodexAppServerClient(Path.Combine(_root, "missing.exe"), TimeSpan.FromSeconds(5), logger);

        await missing.FetchAsync(codexHome, refreshToken: false, CancellationToken.None);
        await missing.FetchAsync(codexHome, refreshToken: false, CancellationToken.None);
        await missing.FetchAsync(Path.Combine(_root, "codex-3"), refreshToken: false, CancellationToken.None);

        // One warning per account: codex-2 once despite two failures, codex-3 once.
        Assert.Equal(2, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Text.Contains("   at ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("he-IL")]
    [InlineData("ar-SA")]
    [InlineData("en-US")]
    public void Settings_status_line_is_the_same_in_every_display_language(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            var text = DebugLoggingPolicy.FormatStatus(new DateTimeOffset(2026, 10, 20, 14, 5, 0, TimeSpan.FromHours(3)));

            Assert.Equal("On until Oct 20, 14:05.", text);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Startup_peek_reads_the_debug_flag_and_its_expiry()
    {
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var dataRoot = AppDataPaths.ResolveRoot(_root);
        Directory.CreateDirectory(dataRoot);
        var settingsPath = Path.Combine(dataRoot, "settings.json");

        Assert.False(JsonSettingsStore.IsDebugLoggingOnAtStartup(now, _root));

        await File.WriteAllTextAsync(settingsPath, """{"debugLoggingEnabled":true,"debugLoggingEnabledAt":"2026-10-10T12:00:00+00:00"}""");
        Assert.True(JsonSettingsStore.IsDebugLoggingOnAtStartup(now, _root));

        await File.WriteAllTextAsync(settingsPath, """{"debugLoggingEnabled":true,"debugLoggingEnabledAt":"2026-10-01T12:00:00+00:00"}""");
        Assert.False(JsonSettingsStore.IsDebugLoggingOnAtStartup(now, _root));

        await File.WriteAllTextAsync(settingsPath, "{ not json");
        Assert.False(JsonSettingsStore.IsDebugLoggingOnAtStartup(now, _root));
    }
}
