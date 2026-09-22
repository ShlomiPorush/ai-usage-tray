using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using costats.App.ViewModels;
using costats.Core.Pulse;
using costats.Core.Tray;

namespace costats.App.Services;

/// <summary>
/// Owns the account-row presentation shared by the tray hover tooltip and the
/// always-on floating panel so their layout and risk colours cannot drift.
/// </summary>
internal static class TrayAccountRowsPresenter
{
    public static void Rebuild(
        Panel panel,
        IReadOnlyList<TrayAccountRow> rows,
        string emptyText = "No AI usage data available")
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(rows);

        panel.Children.Clear();
        if (rows.Count == 0)
        {
            var empty = new TextBlock { FontSize = 12, Text = emptyText };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            panel.Children.Add(empty);
            return;
        }

        foreach (var row in rows)
        {
            var line = new Grid
            {
                Margin = new Thickness(0, 2, 0, 2),
                ToolTip = $"{row.Label}  {row.WindowsText}"
            };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            line.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(UsedColor(row.WorstUsedPercent)))
            });

            var label = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Text = row.Label,
                VerticalAlignment = VerticalAlignment.Center
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            Grid.SetColumn(label, 1);
            line.Children.Add(label);

            var text = new TextBlock
            {
                FontSize = 12,
                Text = "  " + row.WindowsText,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            Grid.SetColumn(text, 2);
            line.Children.Add(text);

            panel.Children.Add(line);
        }
    }

    private static string UsedColor(double? usedPercent) => usedPercent is { } used
        ? BandPalette.Vivid(UsageBands.Of(used))
        : "#9CA3AF";
}
