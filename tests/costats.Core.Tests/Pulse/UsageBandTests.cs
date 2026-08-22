using costats.Core.Pulse;
using Xunit;

namespace costats.Core.Tests.Pulse;

public sealed class UsageBandTests
{
    [Theory]
    [InlineData(0, UsageBand.Green)]
    [InlineData(49, UsageBand.Green)]
    [InlineData(49.9, UsageBand.Green)]
    [InlineData(50, UsageBand.Yellow)]
    [InlineData(74, UsageBand.Yellow)]
    [InlineData(74.9, UsageBand.Yellow)]
    [InlineData(75, UsageBand.Orange)]
    [InlineData(89, UsageBand.Orange)]
    [InlineData(89.9, UsageBand.Orange)]
    [InlineData(90, UsageBand.Red)]
    [InlineData(100, UsageBand.Red)]
    public void Of_bands_by_the_used_number_alone(double usedPercent, UsageBand expected)
    {
        Assert.Equal(expected, UsageBands.Of(usedPercent));
    }

    [Theory]
    [InlineData(0, "OK")]
    [InlineData(49, "OK")]
    [InlineData(50, "Moderate")]
    [InlineData(74, "Moderate")]
    [InlineData(75, "Near limit")]
    [InlineData(89, "Near limit")]
    [InlineData(90, "At limit")]
    [InlineData(100, "At limit")]
    public void StatusText_follows_the_same_edges_as_the_colours(double usedPercent, string expected)
    {
        Assert.Equal(expected, UsageBands.StatusText(usedPercent));
    }

    /// <summary>
    /// The edges are shared constants so no surface can drift from another.
    /// </summary>
    [Fact]
    public void The_band_edges_are_50_75_and_90()
    {
        Assert.Equal(50, UsageBands.YellowFrom);
        Assert.Equal(75, UsageBands.OrangeFrom);
        Assert.Equal(90, UsageBands.RedFrom);
    }
}
