using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using costats.App.ViewModels;
using costats.Core.Tray;

namespace costats.App.Services;

/// <summary>
/// Compact, movable status bar that stays above other windows. It is separate
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

    public TrayStatusPanelWindow(SettingsViewModel settingsViewModel)
    {
        InitializeComponent();
        _settingsViewModel = settingsViewModel;
    }

    /// <summary>
    /// Rebuilds the compact rows. Returns true when the panel was newly shown,
    /// allowing the tray host to choose an initial position exactly once.
    /// </summary>
    public bool Update(IReadOnlyList<TrayCompactRow> rows)
    {
        StatusRowsPanel.Children.Clear();

        IReadOnlyList<TrayCompactRow> materialized = rows.Count == 0
            ? [new TrayCompactRow("AI Usage Tray", "no data", null)]
            : rows;

        for (var index = 0; index < materialized.Count; index += 2)
        {
            var line = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, index == 0 ? 0 : 2, 0, 0)
            };

            AddAccount(line, materialized[index]);
            if (index + 1 < materialized.Count)
            {
                line.Children.Add(new TextBlock
                {
                    Text = "  |  ",
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                });
                ((TextBlock)line.Children[^1]).SetResourceReference(
                    TextBlock.ForegroundProperty,
                    "TextSecondaryBrush");
                AddAccount(line, materialized[index + 1]);
            }

            StatusRowsPanel.Children.Add(line);
        }

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

    private static void AddAccount(Panel line, TrayCompactRow row)
    {
        line.Children.Add(new TextBlock
        {
            Text = $"{row.Label}: {row.StatusText}",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        ((TextBlock)line.Children[^1]).SetResourceReference(
            TextBlock.ForegroundProperty,
            AccountColourResource(row));
    }

    private static string AccountColourResource(TrayCompactRow row)
    {
        if (row.Label.StartsWith("Claude", StringComparison.OrdinalIgnoreCase))
        {
            return "FloatingPanelClaudeBrush";
        }

        if (row.Label.Contains("GLM", StringComparison.OrdinalIgnoreCase) ||
            row.Label.Contains("Z.AI", StringComparison.OrdinalIgnoreCase))
        {
            return "FloatingPanelZaiBrush";
        }

        return "FloatingPanelCodexBrush";
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
