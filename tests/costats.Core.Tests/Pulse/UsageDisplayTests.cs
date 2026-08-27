using costats.Core.Pulse;
using Xunit;

namespace costats.Core.Tests.Pulse;

public sealed class UsageDisplayTests
{
    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(20, 0.2, 0.8)]
    [InlineData(80, 0.8, 0.2)]
    [InlineData(100, 1, 0)]
    public void Progress_follows_the_selected_display_mode(
        double usedPercent,
        double expectedUsedProgress,
        double expectedRemainingProgress)
    {
        Assert.Equal(expectedUsedProgress, UsageDisplay.Progress(usedPercent, false), 6);
        Assert.Equal(expectedRemainingProgress, UsageDisplay.Progress(usedPercent, true), 6);
    }

    [Theory]
    [InlineData(-10, 0, 100)]
    [InlineData(35, 35, 65)]
    [InlineData(110, 100, 0)]
    public void Percent_clamps_before_converting_to_remaining(
        double usedPercent,
        double expectedUsed,
        double expectedRemaining)
    {
        Assert.Equal(expectedUsed, UsageDisplay.Percent(usedPercent, false));
        Assert.Equal(expectedRemaining, UsageDisplay.Percent(usedPercent, true));
    }
}
