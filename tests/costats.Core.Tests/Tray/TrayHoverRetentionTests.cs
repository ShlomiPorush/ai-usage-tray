using costats.Core.Tray;
using Xunit;

namespace costats.Core.Tests.Tray;

public sealed class TrayHoverRetentionTests
{
    private static readonly DateTimeOffset Baseline =
        new(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Pointer_over_icon_keeps_hover_visible_without_a_time_limit()
    {
        var retention = new TrayHoverRetention(TimeSpan.FromMilliseconds(300));
        retention.MarkTrayActivity(Baseline);

        Assert.True(retention.ShouldRemainVisible(Baseline.AddHours(1), pointerIsOverTrayIcon: true));
    }

    [Fact]
    public void Hover_closes_only_after_pointer_leaves_and_grace_period_elapses()
    {
        var retention = new TrayHoverRetention(TimeSpan.FromMilliseconds(300));
        retention.MarkTrayActivity(Baseline);

        Assert.True(retention.ShouldRemainVisible(Baseline.AddMilliseconds(299), pointerIsOverTrayIcon: false));
        Assert.False(retention.ShouldRemainVisible(Baseline.AddMilliseconds(301), pointerIsOverTrayIcon: false));
    }
}
