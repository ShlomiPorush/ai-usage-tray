namespace costats.Core.Tray;

/// <summary>
/// Keeps the tray hover panel visible while the pointer remains over the icon,
/// with a short grace period for the boundary between timer samples.
/// </summary>
public sealed class TrayHoverRetention
{
    private readonly TimeSpan _exitGracePeriod;
    private DateTimeOffset _lastTrayActivity = DateTimeOffset.MinValue;

    public TrayHoverRetention(TimeSpan exitGracePeriod)
    {
        if (exitGracePeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(exitGracePeriod));
        }

        _exitGracePeriod = exitGracePeriod;
    }

    public void MarkTrayActivity(DateTimeOffset now) => _lastTrayActivity = now;

    public bool ObserveTrayMouseMove(DateTimeOffset now, bool pointerIsOverTrayIcon)
    {
        if (!pointerIsOverTrayIcon)
        {
            return false;
        }

        MarkTrayActivity(now);
        return true;
    }

    public bool ShouldRemainVisible(DateTimeOffset now, bool pointerIsOverTrayIcon)
    {
        var sinceLastActivity = now - _lastTrayActivity;
        return pointerIsOverTrayIcon ||
               sinceLastActivity >= TimeSpan.Zero && sinceLastActivity <= _exitGracePeriod;
    }
}
