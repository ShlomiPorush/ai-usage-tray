namespace costats.Application.Windowing;

/// <summary>A window rectangle expressed in WPF device-independent pixels.</summary>
public readonly record struct WindowBounds(double Left, double Top, double Width, double Height);

/// <summary>Fits and centers a window inside the usable desktop area.</summary>
public static class WindowPlacementCalculator
{
    public static WindowBounds FitCentered(
        WindowBounds workArea,
        double desiredWidth,
        double desiredHeight,
        double minWidth,
        double minHeight,
        double margin = 16)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea));
        }

        margin = Math.Max(0, margin);
        var availableWidth = Math.Max(1, workArea.Width - (margin * 2));
        var availableHeight = Math.Max(1, workArea.Height - (margin * 2));

        // On a genuinely tiny display, reachability wins over the declared
        // minimum size. On normal displays, retain the useful minimum.
        var width = availableWidth >= minWidth
            ? Math.Clamp(desiredWidth, minWidth, availableWidth)
            : availableWidth;
        var height = availableHeight >= minHeight
            ? Math.Clamp(desiredHeight, minHeight, availableHeight)
            : availableHeight;

        return new WindowBounds(
            workArea.Left + ((workArea.Width - width) / 2),
            workArea.Top + ((workArea.Height - height) / 2),
            width,
            height);
    }
}
