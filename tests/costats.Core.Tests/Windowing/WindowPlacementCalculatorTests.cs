using costats.Application.Windowing;
using Xunit;

namespace costats.Core.Tests.Windowing;

public sealed class WindowPlacementCalculatorTests
{
    [Fact]
    public void FitCentered_ShrinksTallWindowAndKeepsEveryEdgeInsideWorkArea()
    {
        var workArea = new WindowBounds(0, 0, 1536, 824);

        var placement = WindowPlacementCalculator.FitCentered(
            workArea,
            desiredWidth: 1180,
            desiredHeight: 900,
            minWidth: 900,
            minHeight: 620);

        Assert.Equal(1180, placement.Bounds.Width);
        Assert.Equal(792, placement.Bounds.Height);
        Assert.Equal(900, placement.MinWidth);
        Assert.Equal(620, placement.MinHeight);
        Assert.True(placement.Bounds.Left >= workArea.Left);
        Assert.True(placement.Bounds.Top >= workArea.Top);
        Assert.True(placement.Bounds.Left + placement.Bounds.Width <= workArea.Left + workArea.Width);
        Assert.True(placement.Bounds.Top + placement.Bounds.Height <= workArea.Top + workArea.Height);
    }

    [Fact]
    public void FitCentered_HonorsAnOffsetWorkArea()
    {
        var placement = WindowPlacementCalculator.FitCentered(
            new WindowBounds(-1920, 40, 1920, 1040),
            desiredWidth: 1180,
            desiredHeight: 900,
            minWidth: 900,
            minHeight: 620);

        Assert.Equal(new WindowBounds(-1550, 110, 1180, 900), placement.Bounds);
        Assert.Equal(900, placement.MinWidth);
        Assert.Equal(620, placement.MinHeight);
    }

    [Fact]
    public void FitCentered_ReducesWindowConstraintsWhenWorkAreaIsSmallerThanTheConfiguredMinimums()
    {
        var placement = WindowPlacementCalculator.FitCentered(
            new WindowBounds(0, 0, 800, 600),
            desiredWidth: 1180,
            desiredHeight: 900,
            minWidth: 900,
            minHeight: 620);

        Assert.Equal(new WindowBounds(16, 16, 768, 568), placement.Bounds);
        Assert.Equal(768, placement.MinWidth);
        Assert.Equal(568, placement.MinHeight);
    }
}
