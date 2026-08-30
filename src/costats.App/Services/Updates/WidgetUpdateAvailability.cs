namespace costats.App.Services.Updates;

public sealed record WidgetUpdateAvailability(bool IsVisible, string Version)
{
    public static WidgetUpdateAvailability Hidden { get; } = new(false, string.Empty);

    public static WidgetUpdateAvailability Apply(
        WidgetUpdateAvailability current,
        UpdateCheckResult result)
    {
        return result.Status switch
        {
            UpdateCheckStatus.UpdateAvailable when result.Update is not null =>
                new WidgetUpdateAvailability(true, result.Update.Version),
            UpdateCheckStatus.UpToDate or UpdateCheckStatus.Disabled => Hidden,
            _ => current
        };
    }
}
