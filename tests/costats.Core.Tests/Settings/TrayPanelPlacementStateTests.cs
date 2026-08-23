using costats.Application.Settings;
using Xunit;

namespace costats.Core.Tests.Settings;

public sealed class TrayPanelPlacementStateTests
{
    [Fact]
    public void Resolve_uses_taskbar_position_until_the_user_moves_the_panel()
    {
        var placement = new TrayPanelPlacementState(new AppSettings());

        var position = placement.Resolve(120, 340);

        Assert.Equal(new TrayPanelPosition(120, 340), position);
    }

    [Fact]
    public void Remember_persists_and_resolves_the_user_position()
    {
        var settings = new AppSettings();
        var placement = new TrayPanelPlacementState(settings);

        placement.Remember(222.5, 444.25);

        Assert.Equal(222.5, settings.ClockPanelLeft);
        Assert.Equal(444.25, settings.ClockPanelTop);
        Assert.Equal(new TrayPanelPosition(222.5, 444.25), placement.Resolve(1, 2));
    }

    [Theory]
    [InlineData(double.NaN, 10)]
    [InlineData(10, double.PositiveInfinity)]
    public void Remember_ignores_non_finite_positions(double left, double top)
    {
        var settings = new AppSettings
        {
            ClockPanelLeft = 25,
            ClockPanelTop = 50
        };
        var placement = new TrayPanelPlacementState(settings);

        placement.Remember(left, top);

        Assert.Equal(new TrayPanelPosition(25, 50), placement.Resolve(1, 2));
    }
}
