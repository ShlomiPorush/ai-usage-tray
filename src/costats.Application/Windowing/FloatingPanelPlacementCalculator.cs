namespace costats.Application.Windowing;

public enum FloatingPanelPosition
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public static class FloatingPanelPlacementCalculator
{
    public const string TopLeftSetting = "top-left";
    public const string TopRightSetting = "top-right";
    public const string BottomLeftSetting = "bottom-left";
    public const string BottomRightSetting = "bottom-right";

    public static string NormalizeSetting(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            TopLeftSetting => TopLeftSetting,
            TopRightSetting => TopRightSetting,
            BottomLeftSetting => BottomLeftSetting,
            BottomRightSetting => BottomRightSetting,
            _ => BottomRightSetting
        };

    public static FloatingPanelPosition ParseSetting(string? value) =>
        NormalizeSetting(value) switch
        {
            TopLeftSetting => FloatingPanelPosition.TopLeft,
            TopRightSetting => FloatingPanelPosition.TopRight,
            BottomLeftSetting => FloatingPanelPosition.BottomLeft,
            _ => FloatingPanelPosition.BottomRight
        };

    public static WindowBounds Place(
        WindowBounds workArea,
        double width,
        double height,
        FloatingPanelPosition position,
        double margin = 12)
    {
        var left = position is FloatingPanelPosition.TopLeft or FloatingPanelPosition.BottomLeft
            ? workArea.Left + margin
            : workArea.Left + workArea.Width - width - margin;
        var top = position is FloatingPanelPosition.TopLeft or FloatingPanelPosition.TopRight
            ? workArea.Top + margin
            : workArea.Top + workArea.Height - height - margin;

        return new WindowBounds(left, top, width, height);
    }

    public static WindowBounds ReanchorAfterSizeChange(
        WindowBounds previousBounds,
        WindowBounds workArea,
        double width,
        double height,
        FloatingPanelPosition position,
        double margin = 12) =>
        Place(workArea, width, height, position, margin);
}
