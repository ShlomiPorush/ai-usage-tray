using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace costats.App.Services;

/// <summary>
/// Draggable, always-on-top two-row clock panel restored from the v1.2.24
/// reference build. The close button requests hiding so the window can be
/// reopened from Settings without recreating it.
/// </summary>
public partial class TrayStatusPanelWindow : Window
{
    private static readonly Brush OpenAiBrush = BrushFrom("#4F8CFF");
    private static readonly Brush ClaudeBrush = BrushFrom("#FF6B4A");
    private static readonly Brush GlmBrush = BrushFrom("#FF000000");
    private static readonly Brush GlmBackgroundBrush = BrushFrom("#FFE3E5E8");
    private static readonly Brush DefaultBrush = BrushFrom("#FFF4F5F7");
    private static readonly Brush SeparatorBrush = BrushFrom("#FF8F949E");

    public event Action<double, double>? PositionChangedByUser;
    public event EventHandler? CloseRequested;

    public TrayStatusPanelWindow()
    {
        InitializeComponent();
    }

    public void Update(string text, double x, double y)
    {
        var rows = (string.IsNullOrWhiteSpace(text) ? "AI Usage Tray: no data" : text).Split('\n');
        RenderRow(RowOneText, rows.ElementAtOrDefault(0) ?? string.Empty);
        RenderRow(RowTwoText, rows.ElementAtOrDefault(1) ?? string.Empty);
        Left = x;
        Top = y;

        if (!IsVisible)
        {
            Show();
        }
    }

    public void UpdateMeasure()
    {
        InvalidateMeasure();
        UpdateLayout();
    }

    public Size GetDesiredSize() => new(Width, Height);

    public void HidePanel()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    private static void RenderRow(TextBlock target, string text)
    {
        target.Inlines.Clear();
        var parts = text.Split(" | ");
        for (var index = 0; index < parts.Length; index++)
        {
            if (index > 0)
            {
                target.Inlines.Add(new Run("  |  ") { Foreground = SeparatorBrush });
            }

            var part = parts[index];
            var provider = part.Split(':', 2)[0].Trim();
            target.Inlines.Add(new Run(part)
            {
                Foreground = ProviderBrush(provider),
                Background = provider.Equals("GLM", StringComparison.OrdinalIgnoreCase)
                    ? GlmBackgroundBrush
                    : Brushes.Transparent
            });
        }
    }

    private static Brush ProviderBrush(string provider) => provider.ToUpperInvariant() switch
    {
        "PA" or "GPT" => OpenAiBrush,
        "CLAUDE" => ClaudeBrush,
        "GLM" => GlmBrush,
        _ => DefaultBrush
    };

    private static Brush BrushFrom(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private void OnPanelMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed || CloseButton.IsMouseOver)
        {
            return;
        }

        DragMove();
        PositionChangedByUser?.Invoke(Left, Top);
        e.Handled = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
