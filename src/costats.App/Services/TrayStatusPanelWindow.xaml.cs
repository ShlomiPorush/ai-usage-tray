using System.Windows;

namespace costats.App.Services;

/// <summary>
/// Always-on-top, non-interactive window that displays the current tray
/// status as readable text next to the system clock. Updates on every
/// PulseState publish so the user can read all three provider quotas at a
/// glance without hovering the tray icon.
///
/// <para>
/// Important limitations:
/// <list type="bullet">
///   <item>This is a normal Win32 window, not a real tray item. It looks like
///         a tray item because Windows draws it flush against the taskbar,
///         but it is a real borderless window owned by AI Usage Tray.</item>
///   <item>It can be dragged by the user. Dragging will re-position it inside
///         the work area but does not persist; the next refresh snaps it
///         back next to the clock.</item>
///   <item>On multi-monitor or non-default taskbar layouts the position is
///         a best-effort fit. Use the tray tooltip for the source of truth.</item>
/// </list>
/// </para>
/// </summary>
public partial class TrayStatusPanelWindow : Window
{
    public TrayStatusPanelWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Updates the rendered text and re-positions the window so it hugs the
    /// bottom-right corner of the work area, just to the left of the tray
    /// clock. Safe to call from any UI thread.
    /// </summary>
    public void Update(string text, double x, double y)
    {
        StatusText.Text = string.IsNullOrWhiteSpace(text) ? "AI Usage Tray · no data" : text;
        Left = x;
        Top = y;
        // Re-show if it was previously hidden.
        if (!IsVisible)
        {
            Show();
        }
    }

    /// <summary>
    /// Forces a layout pass so the panel measures itself according to the
    /// current text content. Call this before <see cref="GetDesiredSize"/>.
    /// </summary>
    public void UpdateMeasure()
    {
        // SizeToContent = WidthAndHeight is set in XAML. We nudge the size by
        // toggling Visibility so the layout system recalculates ActualWidth /
        // ActualHeight for the new content.
        InvalidateMeasure();
        UpdateLayout();
    }

    /// <summary>
    /// Returns the panel's current measured size, after a layout pass.
    /// Falls back to the design-time defaults when ActualWidth/Height are 0.
    /// </summary>
    public Size GetDesiredSize() => new(
        ActualWidth > 0 ? ActualWidth : 260,
        ActualHeight > 0 ? ActualHeight : 22);

    /// <summary>
    /// Hides the panel without closing it. The window stays alive so the
    /// next Update call can re-show it cheaply.
    /// </summary>
    public void HidePanel()
    {
        if (IsVisible) Hide();
    }
}
