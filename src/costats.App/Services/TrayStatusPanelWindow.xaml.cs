using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using costats.Application.Settings;
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
    private const double ColumnGap = 8;
    // Border (2), horizontal padding (15), close button (22), and button margin (2).
    private const double HorizontalChrome = 41;

    private readonly SettingsViewModel _settingsViewModel;
    private bool _hasSize;
    private double _cellWidth = 360;
    private Size? _sizeBeforeMove;

    public bool IsManuallyPositioned { get; private set; }

    public TrayStatusPanelWindow(SettingsViewModel settingsViewModel, AppSettings settings)
    {
        InitializeComponent();
        _settingsViewModel = settingsViewModel;
        if (settings.FloatingPanelWidth is { } width && double.IsFinite(width) && width >= MinWidth &&
            settings.FloatingPanelHeight is { } height && double.IsFinite(height) && height >= MinHeight)
        {
            Width = Math.Min(width, SystemParameters.WorkArea.Width);
            Height = Math.Min(height, SystemParameters.WorkArea.Height);
            _hasSize = true;
        }

        SourceInitialized += (_, _) =>
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowProc);
        StatusScrollViewer.SizeChanged += (_, _) => UpdateColumns();
    }

    /// <summary>
    /// Rebuilds the account rows. Returns true when the panel was newly shown,
    /// allowing the tray host to choose an initial position exactly once.
    /// </summary>
    public bool Update(IReadOnlyList<TrayAccountRow> rows)
    {
        TrayAccountRowsPresenter.Rebuild(StatusRowsPanel, rows);
        _cellWidth = 0;
        foreach (FrameworkElement child in StatusRowsPanel.Children)
        {
            child.Margin = new Thickness(0, 2, 0, 2);
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _cellWidth = Math.Max(_cellWidth, child.DesiredSize.Width);
        }

        // Reserve the inter-column gap when measuring so the default layout does
        // not truncate the widest provider just to make room for its separator.
        _cellWidth = Math.Max(240, _cellWidth + ColumnGap);
        if (!_hasSize)
        {
            var columns = Math.Min(2, Math.Max(1, rows.Count));
            Width = Math.Min(Math.Ceiling(_cellWidth * columns) + HorizontalChrome + 2, SystemParameters.WorkArea.Width);
            columns = Math.Max(1, (int)((Width - HorizontalChrome) / _cellWidth));
            StatusRowsPanel.Columns = columns;
            var rowHeight = StatusRowsPanel.Children.Cast<FrameworkElement>()
                .Max(child => child.DesiredSize.Height);
            Height = Math.Clamp(Math.Ceiling(Math.Max(1, rows.Count) / (double)columns) * Math.Ceiling(rowHeight) + 22,
                MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height));
            _hasSize = rows.Count > 0;
        }
        UpdateColumns();

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

    private void UpdateColumns()
    {
        if (StatusScrollViewer.ActualWidth <= 0)
        {
            return;
        }

        StatusRowsPanel.Columns = Math.Clamp(
            (int)((StatusScrollViewer.ActualWidth + 1) / _cellWidth),
            1, Math.Max(1, StatusRowsPanel.Children.Count));

        foreach (var (child, index) in StatusRowsPanel.Children.Cast<FrameworkElement>().Select((child, index) => (child, index)))
        {
            child.Margin = new Thickness(
                0,
                2,
                StatusRowsPanel.Columns > 1 && index % StatusRowsPanel.Columns != StatusRowsPanel.Columns - 1 ? ColumnGap : 0,
                2);
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int hitTest = 0x0084;
        const int enterSizeMove = 0x0231;
        const int exitSizeMove = 0x0232;
        if (message == enterSizeMove)
        {
            // Prevent the tray host from re-anchoring the window while an edge is dragged.
            IsManuallyPositioned = true;
            _sizeBeforeMove = new Size(ActualWidth, ActualHeight);
        }
        else if (message == exitSizeMove && _sizeBeforeMove is { } originalSize)
        {
            _sizeBeforeMove = null;
            if (originalSize != new Size(ActualWidth, ActualHeight))
            {
                _hasSize = true;
                _settingsViewModel.SaveFloatingPanelSize(ActualWidth, ActualHeight);
            }
        }
        else if (message == hitTest)
        {
            var packed = lParam.ToInt64();
            var point = PointFromScreen(new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff)));
            if (point.X < 0 || point.X > ActualWidth || point.Y < 0 || point.Y > ActualHeight)
            {
                return IntPtr.Zero;
            }
            const double edge = 6;
            var left = point.X >= 0 && point.X < edge;
            var right = point.X <= ActualWidth && point.X > ActualWidth - edge;
            var top = point.Y >= 0 && point.Y < edge;
            var bottom = point.Y <= ActualHeight && point.Y > ActualHeight - edge;
            var hit = top ? (left ? 13 : right ? 14 : 12)
                : bottom ? (left ? 16 : right ? 17 : 15)
                : left ? 10 : right ? 11 : 0;
            if (hit != 0)
            {
                handled = true;
                return new IntPtr(hit);
            }
        }

        return IntPtr.Zero;
    }

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

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed || IsInteractiveControl(e.OriginalSource as DependencyObject))
        {
            return;
        }

        try
        {
            e.Handled = true;
            DragMove();
            IsManuallyPositioned = true;
        }
        catch (InvalidOperationException)
        {
            // DragMove can race with a close or display-layout change.
        }
    }

    private static bool IsInteractiveControl(DependencyObject? source)
    {
        for (var current = source; current is not null;
             current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or ScrollBar)
            {
                return true;
            }
        }

        return false;
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
