namespace costats.Application.Settings;

/// <summary>
/// Keeps the draggable clock panel at the user's saved position while falling
/// back to the taskbar-derived position until it has been moved.
/// </summary>
public sealed class TrayPanelPlacementState
{
    private readonly AppSettings _settings;

    public TrayPanelPlacementState(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public TrayPanelPosition Resolve(double suggestedLeft, double suggestedTop)
    {
        return HasSavedPosition()
            ? new TrayPanelPosition(_settings.ClockPanelLeft!.Value, _settings.ClockPanelTop!.Value)
            : new TrayPanelPosition(suggestedLeft, suggestedTop);
    }

    public void Remember(double left, double top)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top))
        {
            return;
        }

        _settings.ClockPanelLeft = left;
        _settings.ClockPanelTop = top;
    }

    private bool HasSavedPosition() =>
        _settings.ClockPanelLeft is { } left &&
        _settings.ClockPanelTop is { } top &&
        double.IsFinite(left) &&
        double.IsFinite(top);
}

public readonly record struct TrayPanelPosition(double Left, double Top);
