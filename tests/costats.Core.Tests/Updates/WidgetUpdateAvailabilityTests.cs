using costats.App.Services.Updates;
using Xunit;

namespace costats.Core.Tests.Updates;

public sealed class WidgetUpdateAvailabilityTests
{
    private static readonly AvailableUpdate Available = new(
        "99.0.0",
        "Important changes",
        "https://github.com/example/example/releases/tag/v99.0.0",
        "package.zip",
        "https://github.com/example/example/releases/download/v99.0.0/package.zip",
        null);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_ShowsAvailableUpdate_FromFreshOrCachedResult(bool fromCache)
    {
        var result = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, Available, fromCache);

        var state = WidgetUpdateAvailability.Apply(WidgetUpdateAvailability.Hidden, result);

        Assert.True(state.IsVisible);
        Assert.Equal("99.0.0", state.Version);
    }

    [Theory]
    [InlineData(UpdateCheckStatus.UpToDate)]
    [InlineData(UpdateCheckStatus.Disabled)]
    public void Apply_HidesBanner_WhenNoUpdateCanBeAvailable(UpdateCheckStatus status)
    {
        var current = new WidgetUpdateAvailability(true, "99.0.0");

        var state = WidgetUpdateAvailability.Apply(current, new UpdateCheckResult(status));

        Assert.Equal(WidgetUpdateAvailability.Hidden, state);
    }

    [Theory]
    [InlineData(UpdateCheckStatus.Skipped)]
    [InlineData(UpdateCheckStatus.AlreadyRunning)]
    [InlineData(UpdateCheckStatus.CheckFailed)]
    public void Apply_PreservesBanner_AfterInconclusiveResult(UpdateCheckStatus status)
    {
        var current = new WidgetUpdateAvailability(true, "99.0.0");

        var state = WidgetUpdateAvailability.Apply(current, new UpdateCheckResult(status));

        Assert.Equal(current, state);
    }
}
