using costats.Application.Diagnostics;
using costats.Application.Settings;
using costats.Infrastructure.Settings;
using costats.Application.Storage;
using Xunit;

namespace costats.Core.Tests.Diagnostics;

public sealed class DebugLoggingPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Off_never_expires()
    {
        Assert.False(DebugLoggingPolicy.IsExpired(false, null, Start));
        Assert.False(DebugLoggingPolicy.IsExpired(false, Start.AddDays(-30), Start));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(6 * 24 + 23, false)]
    [InlineData(7 * 24, true)]
    [InlineData(30 * 24, true)]
    public void On_expires_seven_days_after_it_was_turned_on(int hoursLater, bool expired)
    {
        Assert.Equal(expired, DebugLoggingPolicy.IsExpired(true, Start, Start.AddHours(hoursLater)));
    }

    [Fact]
    public void On_without_a_start_time_counts_as_expired()
    {
        Assert.True(DebugLoggingPolicy.IsExpired(true, null, Start));
    }

    [Fact]
    public void A_clock_set_back_a_little_does_not_switch_it_off_but_a_start_far_in_the_future_does()
    {
        Assert.False(DebugLoggingPolicy.IsExpired(true, Start.AddHours(2), Start));
        Assert.True(DebugLoggingPolicy.IsExpired(true, Start.AddDays(3), Start));
    }

    [Fact]
    public void Expiry_time_is_seven_days_after_the_start()
    {
        Assert.Equal(Start.AddDays(7), DebugLoggingPolicy.ExpiresAt(Start));
    }

    [Fact]
    public void Debug_logging_is_off_by_default()
    {
        var settings = new AppSettings();

        Assert.False(settings.DebugLoggingEnabled);
        Assert.Null(settings.DebugLoggingEnabledAt);
    }

    [Fact]
    public async Task Settings_written_before_debug_logging_existed_load_with_it_off()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "ai-usage-tray-debug-default", Guid.NewGuid().ToString("N"));
        try
        {
            var dataRoot = AppDataPaths.ResolveRoot(basePath);
            Directory.CreateDirectory(dataRoot);
            await File.WriteAllTextAsync(Path.Combine(dataRoot, "settings.json"), """{ "refreshMinutes": 7 }""");

            var settings = await new JsonSettingsStore(basePath: basePath).LoadAsync(CancellationToken.None);

            Assert.Equal(7, settings.RefreshMinutes);
            Assert.False(settings.DebugLoggingEnabled);
            Assert.Null(settings.DebugLoggingEnabledAt);
        }
        finally
        {
            try { Directory.Delete(basePath, recursive: true); } catch { /* best effort */ }
        }
    }
}
