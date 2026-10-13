using costats.Core.Tray;
using Xunit;

namespace costats.Core.Tests.Tray;

public sealed class TrayIconHitTestTests
{
    // Measured on a 1920 px wide Hebrew Windows 11 taskbar: the shell reported
    // 1439..1471 while the icon was drawn at 449..481.
    private const int ReportedLeft = 1439;
    private const int ReportedRight = 1471;
    private const int Top = 1032;
    private const int Bottom = 1080;

    [Fact]
    public void Left_to_right_taskbar_uses_the_reported_rectangle()
    {
        Assert.True(TrayIconHitTest.IsPointerOverIcon(1450, 1050, ReportedLeft, Top, ReportedRight, Bottom));
        Assert.False(TrayIconHitTest.IsPointerOverIcon(460, 1050, ReportedLeft, Top, ReportedRight, Bottom));
    }

    [Fact]
    public void Right_to_left_taskbar_mirrors_the_reported_rectangle()
    {
        Assert.True(TrayIconHitTest.IsPointerOverIcon(460, 1050, ReportedLeft, Top, ReportedRight, Bottom, 0, 1920));
        Assert.False(TrayIconHitTest.IsPointerOverIcon(1450, 1050, ReportedLeft, Top, ReportedRight, Bottom, 0, 1920));
    }

    [Fact]
    public void Mirrored_edges_stay_half_open()
    {
        Assert.True(TrayIconHitTest.IsPointerOverIcon(449, 1050, ReportedLeft, Top, ReportedRight, Bottom, 0, 1920));
        Assert.False(TrayIconHitTest.IsPointerOverIcon(481, 1050, ReportedLeft, Top, ReportedRight, Bottom, 0, 1920));
    }

    [Fact]
    public void Right_to_left_taskbar_on_a_secondary_monitor_mirrors_within_that_taskbar()
    {
        // Taskbar spans 1920..3840; the icon is reported at 3359..3391 and drawn at 2369..2401.
        Assert.True(TrayIconHitTest.IsPointerOverIcon(2380, 1050, 3359, Top, 3391, Bottom, 1920, 3840));
    }

    [Fact]
    public void Pointer_outside_the_taskbar_height_never_matches()
    {
        Assert.False(TrayIconHitTest.IsPointerOverIcon(460, 1000, ReportedLeft, Top, ReportedRight, Bottom, 0, 1920));
    }
}
