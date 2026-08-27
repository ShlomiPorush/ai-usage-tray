using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using costats.App.ViewModels;
using costats.Core.Tray;

namespace costats.App.Services;

/// <summary>
/// Movable status panel that stays above other windows. It is separate
/// from both the hover tooltip and the full account widget.
/// </summary>
public partial class TrayStatusPanelWindow : Window
{
    private static readonly IntPtr TopmostWindow = new(-1);
    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoActivate = 0x0010;
    private const uint ShowWindow = 0x0040;

    private readonly SettingsViewModel _settingsViewModel;

    public bool IsManuallyPositioned { get; private set; }

    public TrayStatusPanelWindow(SettingsViewModel settingsViewModel)
    {
        InitializeComponent();
        _settingsViewModel = settingsViewModel;
    }

    /// <summary>
    /// Rebuilds the account rows. Returns true when the panel was newly shown,
    /// allowing the tray host to choose an initial position exactly once.
    /// </summary>
    public bool Update(IReadOnlyList<TrayAccountRow> rows)
    {
        TrayAccountRowsPresenter.Rebuild(StatusRowsPanel, rows);

        var newlyShown = !IsVisible;
        if (newlyShown)
        {
            Show();
        }

        UpdateLayout();
        return newlyShown;
    }

    public void HidePanel()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    public void ResetManualPosition() => IsManuallyPositioned = false;

    /// <summary>
    /// Raises the panel above other topmost windows without taking keyboard
    /// focus from the application the user is currently working in.
    /// </summary>
    public void BringToFront()
    {
        if (!IsVisible)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            SetWindowPos(handle, TopmostWindow, 0, 0, 0, 0, NoMove | NoSize | NoActivate | ShowWindow);
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
            IsManuallyPositioned = true;
        }
        catch (InvalidOperationException)
        {
            // DragMove can race with a close or display-layout change.
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _settingsViewModel.ShowFloatingStatusPanel = false;
        HidePanel();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
