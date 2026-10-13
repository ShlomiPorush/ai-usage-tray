namespace costats.Core.Tray;

/// <summary>
/// Decides whether the pointer is over the tray icon. On a right-to-left
/// taskbar (for example a Hebrew or Arabic display language),
/// Shell_NotifyIconGetRect reports the icon mirrored across the taskbar, so
/// the reported rectangle is flipped back before the test.
/// </summary>
public static class TrayIconHitTest
{
    public static bool IsPointerOverIcon(
        int pointerX,
        int pointerY,
        int iconLeft,
        int iconTop,
        int iconRight,
        int iconBottom,
        int? rightToLeftTaskbarLeft = null,
        int? rightToLeftTaskbarRight = null)
    {
        if (rightToLeftTaskbarLeft is int taskbarLeft && rightToLeftTaskbarRight is int taskbarRight)
        {
            var mirroredLeft = taskbarLeft + taskbarRight - iconRight;
            var mirroredRight = taskbarLeft + taskbarRight - iconLeft;
            iconLeft = mirroredLeft;
            iconRight = mirroredRight;
        }

        return pointerX >= iconLeft && pointerX < iconRight &&
               pointerY >= iconTop && pointerY < iconBottom;
    }
}
