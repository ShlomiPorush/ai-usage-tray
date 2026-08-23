namespace costats.Application.Windowing;

/// <summary>A window rectangle expressed in WPF device-independent pixels.</summary>
public readonly record struct WindowBounds(double Left, double Top, double Width, double Height);

/// <summary>A fitted window rectangle and the constraints required to preserve it.</summary>
public readonly record struct WindowPlacement(WindowBounds Bounds, double MinWidth, double MinHeight);

/// <summary>Fits and centers a window inside the usable desktop area.</summary>
public static class WindowPlacementCalculator
{
    public static WindowPlacement FitCentered(
        WindowBounds workArea,
        double desiredWidth,
        double desiredHeight,
        double minWidth,
        double minHeight,
        double margin = 16)
    {
        EnsurePositiveFinite(workArea.Width, nameof(workArea));
        EnsurePositiveFinite(workArea.Height, nameof(workArea));
        EnsurePositiveFinite(desiredWidth, nameof(desiredWidth));
        EnsurePositiveFinite(desiredHeight, nameof(desiredHeight));
        EnsurePositiveFinite(minWidth, nameof(minWidth));
        EnsurePositiveFinite(minHeight, nameof(minHeight));

        if (!double.IsFinite(margin) || margin < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(margin));
        }

        var availableWidth = Math.Max(1, workArea.Width - (margin * 2));
        var availableHeight = Math.Max(1, workArea.Height - (margin * 2));
        var effectiveMinWidth = Math.Min(minWidth, availableWidth);
        var effectiveMinHeight = Math.Min(minHeight, availableHeight);
        var width = Math.Clamp(desiredWidth, effectiveMinWidth, availableWidth);
        var height = Math.Clamp(desiredHeight, effectiveMinHeight, availableHeight);

        return new WindowPlacement(
            new WindowBounds(
                workArea.Left + ((workArea.Width - width) / 2),
                workArea.Top + ((workArea.Height - height) / 2),
                width,
                height),
            effectiveMinWidth,
            effectiveMinHeight);
    }

    private static void EnsurePositiveFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
