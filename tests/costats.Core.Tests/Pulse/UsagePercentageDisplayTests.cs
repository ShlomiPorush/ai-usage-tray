using costats.Core.Pulse;
using Xunit;

namespace costats.Core.Tests.Pulse;

public sealed class UsagePercentageDisplayTests
{
    [Theory]
    [InlineData(34, false, 34)]
    [InlineData(34, true, 66)]
    [InlineData(-5, true, 100)]
    [InlineData(110, true, 0)]
    public void Value_converts_and_clamps_the_selected_view(
        double usedPercent,
        bool showLeft,
        double expected)
    {
        Assert.Equal(expected, UsagePercentageDisplay.Value(usedPercent, showLeft));
    }

    [Fact]
    public void Labels_make_the_selected_meaning_explicit()
    {
        Assert.Equal("34% used", UsagePercentageDisplay.Label(34, showLeft: false));
        Assert.Equal("66% left", UsagePercentageDisplay.Label(34, showLeft: true));
    }
}
