using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using costats.App.Services;
using costats.Application.Settings;
using costats.Core.Tray;

class Program
{
    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [STAThread]
    static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/AIUsageTray;component/Themes/ThemeDark.xaml", UriKind.Relative)
        });
        var rows = new[]
        {
            new TrayAccountRow("Claude", "Weekly 26% · 2.9d  |  Session 28% · 3h08m", 26),
            new TrayAccountRow("GLM", "Weekly 2% · 1.8d  |  Session 99% · 3h13m", 99),
            new TrayAccountRow("GPT", "Weekly 59% · 5.8d  |  Session 100% · 3h13m", 100),
            new TrayAccountRow("PA", "Weekly 20% · 4.5d", 20)
        };
        var window = new TrayStatusPanelWindow(null!, new AppSettings());
        window.Update(rows);
        var panel = (UniformGrid)window.FindName("StatusRowsPanel");
        Pump(window);
        Check(panel.Columns == 2, $"Default columns: {panel.Columns}");
        foreach (Grid row in panel.Children)
        {
            var text = (TextBlock)row.Children[2];
            var available = text.ActualWidth;
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Check(text.DesiredSize.Width <= available + 1, "Default columns must fit complete status text including the column gap");
        }
        Pump(window);
        Capture(window, "two-columns.png");
        var originalWidth = window.Width;
        window.Width = 450;
        window.Height = 115;
        Pump(window);
        Check(panel.Columns == 1, "Narrow window should use one column");
        Capture(window, "one-column.png");
        window.Width = originalWidth;
        window.Height = 65;
        window.Update(rows);
        Pump(window);
        Check(panel.Columns == 2, "Wide window should return to two columns");
        Check(Math.Abs(window.Height - 65) < 1, "Refresh must preserve user height within pixel rounding");
        var hwnd = new WindowInteropHelper(window).Handle;
        var point = window.PointToScreen(new Point(window.ActualWidth - 2, window.ActualHeight - 2));
        var packed = new IntPtr(((int)point.Y << 16) | ((int)point.X & 0xffff));
        Check(SendMessage(hwnd, 0x84, IntPtr.Zero, packed).ToInt32() == 17,
            "Bottom right corner must expose the native resize hit target");
        foreach (var (position, expected) in new (Point, int)[]
        {
            (new Point(2, 2), 13), (new Point(window.ActualWidth / 2, 2), 12),
            (new Point(window.ActualWidth - 2, 2), 14),
            (new Point(2, window.ActualHeight / 2), 10),
            (new Point(window.ActualWidth - 2, window.ActualHeight / 2), 11),
            (new Point(2, window.ActualHeight - 2), 16),
            (new Point(window.ActualWidth / 2, window.ActualHeight - 2), 15)
        })
        {
            var screen = window.PointToScreen(position);
            var coordinates = new IntPtr(((int)screen.Y << 16) | ((int)screen.X & 0xffff));
            Check(SendMessage(hwnd, 0x84, IntPtr.Zero, coordinates).ToInt32() == expected,
                $"Native resize target at {position}");
        }
        // Enter/exit move without resizing must not try to persist dimensions.
        // A null view model keeps the fixture isolated from user settings and providers.
        SendMessage(hwnd, 0x231, IntPtr.Zero, IntPtr.Zero);
        SendMessage(hwnd, 0x232, IntPtr.Zero, IntPtr.Zero);
        Check(window.IsManuallyPositioned, "A manually moved panel must not be re-anchored by the tray host");
        window.Update([]);
        Pump(window);
        Check(panel.Children.Count == 1, "Empty state remains visible");
        window.Close();
        var restored = new TrayStatusPanelWindow(null!, new AppSettings
        {
            FloatingPanelWidth = 650,
            FloatingPanelHeight = 90
        });
        restored.Update(rows);
        Pump(restored);
        Check(Math.Abs(restored.Width - 650) < 1 && Math.Abs(restored.Height - 90) < 1, "Saved dimensions must restore");
        restored.Close();
        var narrow = new TrayStatusPanelWindow(null!, new AppSettings
        {
            FloatingPanelWidth = 462,
            FloatingPanelHeight = 79
        });
        narrow.Update(rows.Take(2).ToArray());
        Pump(narrow);
        Capture(narrow, "two-rows-narrow.png");
        narrow.Width = 280;
        Pump(narrow);
        var narrowPanel = (UniformGrid)narrow.FindName("StatusRowsPanel");
        foreach (Grid row in narrowPanel.Children)
        {
            var text = (TextBlock)row.Children[2];
            var right = text.TranslatePoint(new Point(text.ActualWidth, 0), narrowPanel).X;
            Check(Math.Abs(right - narrowPanel.ActualWidth) < 1, "Single-column text must fill the space before X without a trailing gutter");
        }
        narrow.Close();
        var invalid = new TrayStatusPanelWindow(null!, new AppSettings
        {
            FloatingPanelWidth = double.NaN,
            FloatingPanelHeight = -100
        });
        invalid.Update([]);
        Pump(invalid);
        invalid.Update(rows);
        Pump(invalid);
        Check(((UniformGrid)invalid.FindName("StatusRowsPanel")).Columns == 2,
            "Invalid saved dimensions and an initial empty state must recover to the default layout");
        invalid.Close();
        Console.WriteLine("PASS: two-column default, narrow/wide reflow, refresh stability, native resize hit target, empty state, saved dimensions.");
    }

    static void Pump(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static void Capture(Window window, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name));
        encoder.Save(file);
    }
}
