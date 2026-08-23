using costats.Application.Windowing;
using Xunit;

namespace costats.Core.Tests.Windowing;

public sealed class WindowPlacementCalculatorTests
{
    [Fact]
    public void FitCentered_ShrinksTallWindowAndKeepsEveryEdgeInsideWorkArea()
    {
        var workArea = new WindowBounds(0, 0, 1536, 824);

        var result = WindowPlacementCalculator.FitCentered(workArea, 1180, 900, 900, 620);

        Assert.Equal(1180, result.Width);
        Assert.Equal(792, result.Height);
        Assert.True(result.Left >= workArea.Left);
        Assert.True(result.Top >= workArea.Top);
        Assert.True(result.Left + result.Width <= workArea.Left + workArea.Width);
        Assert.True(result.Top + result.Height <= workArea.Top + workArea.Height);
    }

    [Fact]
    public void FitCentered_HonorsAnOffsetMonitorWorkArea()
    {
        var result = WindowPlacementCalculator.FitCentered(
            new WindowBounds(-1920, 40, 1920, 1040), 1180, 900, 900, 620);

        Assert.Equal(-1550, result.Left);
        Assert.Equal(110, result.Top);
        Assert.Equal(1180, result.Width);
        Assert.Equal(900, result.Height);
    }

    [Fact]
    public void FitCentered_PrioritizesReachabilityOnVerySmallDisplays()
    {
        var result = WindowPlacementCalculator.FitCentered(
            new WindowBounds(0, 0, 800, 600), 1180, 900, 900, 620);

        Assert.Equal(new WindowBounds(16, 16, 768, 568), result);
    }
}
