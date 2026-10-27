using System.Windows;
using System.Windows.Media;

namespace costats.App.Controls;

/// <summary>
/// A thin bar split into one segment per value, left to right, each as wide as
/// its share of the sum. Segment <c>i</c> is drawn in <see cref="Brush"/> at
/// <see cref="SegmentOpacities"/>[i], so a legend can repeat the same shades.
/// </summary>
public sealed class UsageSplitBar : FrameworkElement
{
    /// <summary>The shade of each segment, by position, lightest first.</summary>
    public static readonly IReadOnlyList<double> SegmentOpacities = [0.2, 0.42, 0.68, 1d];

    /// <summary>Smallest width a non-zero segment is drawn at, so it stays visible.</summary>
    private const double MinSegment = 3d;

    private const double Gap = 3d;

    /// <summary>The values to split the bar by, in segment order.</summary>
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IReadOnlyList<double>),
        typeof(UsageSplitBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The colour every segment is a shade of.</summary>
    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush),
        typeof(Brush),
        typeof(UsageSplitBar),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The empty bar, drawn when every value is zero.</summary>
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush),
        typeof(Brush),
        typeof(UsageSplitBar),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <inheritdoc cref="ValuesProperty"/>
    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <inheritdoc cref="BrushProperty"/>
    public Brush Brush
    {
        get => (Brush)GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    /// <inheritdoc cref="TrackBrushProperty"/>
    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>
    /// Where each segment starts and how wide it is, for a bar of
    /// <paramref name="width"/>. Zero values get no segment; every other one
    /// is at least <see cref="MinSegment"/> wide, taken from the widest.
    /// </summary>
    public static IReadOnlyList<(int Index, double Left, double Width)> Layout(IReadOnlyList<double> values, double width)
    {
        ArgumentNullException.ThrowIfNull(values);
        var shown = Enumerable.Range(0, values.Count)
            .Where(index => double.IsFinite(values[index]) && values[index] > 0d)
            .ToList();
        var sum = shown.Sum(index => values[index]);
        if (shown.Count == 0 || sum <= 0d || width <= 0d)
        {
            return [];
        }

        var available = Math.Max(0d, width - (Gap * (shown.Count - 1)));
        var widths = shown.Select(index => available * values[index] / sum).ToArray();

        // Tiny shares are widened to stay visible; the widest segment pays.
        var widest = Array.IndexOf(widths, widths.Max());
        for (var i = 0; i < widths.Length; i++)
        {
            if (i != widest && widths[i] < MinSegment)
            {
                widths[widest] -= MinSegment - widths[i];
                widths[i] = MinSegment;
            }
        }

        widths[widest] = Math.Max(MinSegment, widths[widest]);
        var segments = new List<(int, double, double)>(shown.Count);
        var cursor = 0d;
        for (var i = 0; i < shown.Count; i++)
        {
            segments.Add((shown[i], cursor, widths[i]));
            cursor += widths[i] + Gap;
        }

        return segments;
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var height = ActualHeight;
        var radius = height / 2d;
        var segments = Layout(Values ?? [], ActualWidth);
        if (segments.Count == 0)
        {
            drawingContext.DrawRoundedRectangle(TrackBrush, null, new Rect(RenderSize), radius, radius);
            return;
        }

        foreach (var (index, left, width) in segments)
        {
            var shade = Brush.Clone();
            shade.Opacity = SegmentOpacities[Math.Min(index, SegmentOpacities.Count - 1)];
            shade.Freeze();
            drawingContext.DrawRoundedRectangle(shade, null, new Rect(left, 0d, width, height), radius, radius);
        }
    }
}
