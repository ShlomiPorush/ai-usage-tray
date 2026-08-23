using System.Globalization;
using System.Windows;
using System.Windows.Media;
using costats.Core.Analytics;

namespace costats.App.Controls;

/// <summary>One provider's line on the daily chart.</summary>
/// <param name="Provider">Picks the accent the line is drawn in.</param>
/// <param name="Values">
/// One value per day of <see cref="UsageChartData.Days"/>, same order and length.
/// </param>
public sealed record UsageChartSeries(UsageProviderKind Provider, IReadOnlyList<double> Values);

/// <summary>
/// Everything <see cref="UsageChart"/> needs for one render pass. The view model
/// builds it; the control only draws it.
/// </summary>
public sealed record UsageChartData
{
    /// <summary>Nothing to draw.</summary>
    public static readonly UsageChartData Empty = new();

    /// <summary>Every day of the selected range, ascending, gaps included.</summary>
    public IReadOnlyList<DateOnly> Days { get; init; } = [];

    /// <summary>One entry per provider that has data in the range.</summary>
    public IReadOnlyList<UsageChartSeries> Series { get; init; } = [];

    /// <summary>Formats a Y axis tick, so the same control serves dollars and tokens.</summary>
    public Func<double, string> AxisLabel { get; init; } = value => value.ToString("0", CultureInfo.InvariantCulture);
}

/// <summary>
/// The daily cost/token chart: a smoothed area series per provider over a
/// continuous day axis, drawn directly with <see cref="DrawingContext"/>.
/// </summary>
/// <remarks>
/// Hand-drawn rather than charted by a library: the app ships no chart
/// dependency, and the whole visual is four gridlines, two curves and five
/// labels. Colours arrive as dependency properties so the XAML can bind them to
/// theme brushes with <c>DynamicResource</c> and a theme switch simply
/// re-renders.
/// </remarks>
public sealed class UsageChart : FrameworkElement
{
    /// <summary>Width reserved on the left for the value axis labels.</summary>
    private const double AxisGutter = 68d;

    /// <summary>Height reserved at the bottom for the date labels.</summary>
    private const double DateGutter = 20d;

    private const double TopPadding = 10d;
    private const double LabelSize = 10d;

    /// <summary>The series to draw.</summary>
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data),
        typeof(UsageChartData),
        typeof(UsageChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Accent for <see cref="UsageProviderKind.Claude"/>.</summary>
    public static readonly DependencyProperty ClaudeBrushProperty = DependencyProperty.Register(
        nameof(ClaudeBrush),
        typeof(Brush),
        typeof(UsageChart),
        new FrameworkPropertyMetadata(Brushes.OrangeRed, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Accent for <see cref="UsageProviderKind.Codex"/>.</summary>
    public static readonly DependencyProperty CodexBrushProperty = DependencyProperty.Register(
        nameof(CodexBrush),
        typeof(Brush),
        typeof(UsageChart),
        new FrameworkPropertyMetadata(Brushes.MediumSeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Accent for <see cref="UsageProviderKind.Zai"/>.</summary>
    public static readonly DependencyProperty ZaiBrushProperty = DependencyProperty.Register(
        nameof(ZaiBrush),
        typeof(Brush),
        typeof(UsageChart),
        new FrameworkPropertyMetadata(Brushes.RoyalBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Gridline colour.</summary>
    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush),
        typeof(Brush),
        typeof(UsageChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Axis label colour.</summary>
    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush),
        typeof(Brush),
        typeof(UsageChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <inheritdoc cref="DataProperty"/>
    public UsageChartData? Data
    {
        get => (UsageChartData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <inheritdoc cref="ClaudeBrushProperty"/>
    public Brush ClaudeBrush
    {
        get => (Brush)GetValue(ClaudeBrushProperty);
        set => SetValue(ClaudeBrushProperty, value);
    }

    /// <inheritdoc cref="CodexBrushProperty"/>
    public Brush CodexBrush
    {
        get => (Brush)GetValue(CodexBrushProperty);
        set => SetValue(CodexBrushProperty, value);
    }

    /// <inheritdoc cref="ZaiBrushProperty"/>
    public Brush ZaiBrush
    {
        get => (Brush)GetValue(ZaiBrushProperty);
        set => SetValue(ZaiBrushProperty, value);
    }

    /// <inheritdoc cref="GridBrushProperty"/>
    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    /// <inheritdoc cref="LabelBrushProperty"/>
    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var width = ActualWidth;
        var height = ActualHeight;
        var data = Data;
        if (width <= AxisGutter + 24 || height <= DateGutter + 40 || data is null || data.Days.Count == 0)
        {
            return;
        }

        var left = AxisGutter;
        var right = width - 6d;
        var top = TopPadding;
        var bottom = height - DateGutter;
        if (right <= left || bottom <= top)
        {
            return;
        }

        var peak = 0d;
        foreach (var series in data.Series)
        {
            foreach (var value in series.Values)
            {
                if (double.IsFinite(value) && value > peak)
                {
                    peak = value;
                }
            }
        }

        // Gridlines, baseline included, with their values on the left. The
        // step is rounded first and the axis top derived from it, so every
        // label is a round number instead of a third of one.
        const int tickCount = 3;
        var axisMax = NiceStep(peak / tickCount) * tickCount;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");

        var gridPen = new Pen(GridBrush, 1d);
        gridPen.Freeze();
        for (var tick = 0; tick <= tickCount; tick++)
        {
            var value = axisMax * tick / tickCount;
            var y = Snap(bottom - ((bottom - top) * tick / tickCount));
            drawingContext.DrawLine(gridPen, new Point(left, y), new Point(right, y));

            var label = Text(data.AxisLabel(value), typeface, dpi, LabelBrush);
            drawingContext.DrawText(label, new Point(left - 8d - label.Width, y - (label.Height / 2d)));
        }

        // Date labels: first, middle, last. Anything denser is unreadable at
        // 90 days and adds nothing at 7.
        DrawDateLabel(drawingContext, data.Days[0], left, bottom, typeface, dpi, HorizontalAlignment.Left);
        if (data.Days.Count > 2)
        {
            DrawDateLabel(
                drawingContext,
                data.Days[data.Days.Count / 2],
                (left + right) / 2d,
                bottom,
                typeface,
                dpi,
                HorizontalAlignment.Center);
        }

        if (data.Days.Count > 1)
        {
            DrawDateLabel(drawingContext, data.Days[^1], right, bottom, typeface, dpi, HorizontalAlignment.Right);
        }

        foreach (var series in data.Series)
        {
            DrawSeries(drawingContext, series, data.Days.Count, left, right, top, bottom, axisMax);
        }
    }

    private void DrawSeries(
        DrawingContext drawingContext,
        UsageChartSeries series,
        int dayCount,
        double left,
        double right,
        double top,
        double bottom,
        double axisMax)
    {
        if (series.Values.Count == 0 || axisMax <= 0d)
        {
            return;
        }

        var step = dayCount > 1 ? (right - left) / (dayCount - 1) : 0d;
        var points = new List<Point>(series.Values.Count);
        for (var index = 0; index < series.Values.Count; index++)
        {
            var value = double.IsFinite(series.Values[index]) ? Math.Max(0d, series.Values[index]) : 0d;
            var x = dayCount > 1 ? left + (step * index) : (left + right) / 2d;
            var y = bottom - ((bottom - top) * Math.Min(1d, value / axisMax));
            points.Add(new Point(x, y));
        }

        var accent = series.Provider switch
        {
            UsageProviderKind.Claude => ClaudeBrush,
            UsageProviderKind.Zai => ZaiBrush,
            _ => CodexBrush
        };
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: true, isClosed: false);
            AppendSmoothCurve(context, points, top, bottom);

            // Close the area down to the baseline so the fill has a floor.
            context.LineTo(new Point(points[^1].X, bottom), isStroked: false, isSmoothJoin: false);
            context.LineTo(new Point(points[0].X, bottom), isStroked: false, isSmoothJoin: false);
        }

        geometry.Freeze();

        var fill = accent.Clone();
        fill.Opacity = 0.22;
        fill.Freeze();
        drawingContext.DrawGeometry(fill, null, geometry);

        // The stroke is drawn on a second, open geometry so the two baseline
        // segments that close the fill are not outlined.
        var line = new StreamGeometry();
        using (var context = line.Open())
        {
            context.BeginFigure(points[0], isFilled: false, isClosed: false);
            AppendSmoothCurve(context, points, top, bottom);
        }

        line.Freeze();
        drawingContext.DrawGeometry(null, new Pen(accent, 1.6d)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        }, line);
    }

    /// <summary>
    /// Appends a Catmull-Rom spline through <paramref name="points"/> as cubic
    /// beziers. Control points are clamped to the plot area so a steep spike
    /// cannot bow the curve below the baseline into negative territory.
    /// </summary>
    private static void AppendSmoothCurve(StreamGeometryContext context, IReadOnlyList<Point> points, double top, double bottom)
    {
        if (points.Count == 1)
        {
            context.LineTo(points[0], isStroked: true, isSmoothJoin: false);
            return;
        }

        for (var index = 0; index < points.Count - 1; index++)
        {
            var previous = points[Math.Max(0, index - 1)];
            var current = points[index];
            var next = points[index + 1];
            var following = points[Math.Min(points.Count - 1, index + 2)];

            var first = new Point(
                current.X + ((next.X - previous.X) / 6d),
                Math.Clamp(current.Y + ((next.Y - previous.Y) / 6d), top, bottom));
            var second = new Point(
                next.X - ((following.X - current.X) / 6d),
                Math.Clamp(next.Y - ((following.Y - current.Y) / 6d), top, bottom));

            context.BezierTo(first, second, next, isStroked: true, isSmoothJoin: true);
        }
    }

    private void DrawDateLabel(
        DrawingContext drawingContext,
        DateOnly day,
        double x,
        double bottom,
        Typeface typeface,
        double dpi,
        HorizontalAlignment alignment)
    {
        var text = Text(UsageNumberFormat.AxisDayLabel(day), typeface, dpi, LabelBrush);
        var offset = alignment switch
        {
            HorizontalAlignment.Right => x - text.Width,
            HorizontalAlignment.Center => x - (text.Width / 2d),
            _ => x
        };

        drawingContext.DrawText(text, new Point(offset, bottom + 4d));
    }

    private static FormattedText Text(string value, Typeface typeface, double dpi, Brush brush) =>
        new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, LabelSize, brush, dpi);

    /// <summary>
    /// Rounds a gridline step up to the nearest value a person would have
    /// picked, so the labels read $400 and $800 rather than $666.67.
    /// </summary>
    private static double NiceStep(double value)
    {
        if (!double.IsFinite(value) || value <= 0d)
        {
            return 1d;
        }

        var magnitude = Math.Pow(10d, Math.Floor(Math.Log10(value)));
        var normalized = value / magnitude;
        var stepped = normalized switch
        {
            <= 1d => 1d,
            <= 1.5d => 1.5d,
            <= 2d => 2d,
            <= 2.5d => 2.5d,
            <= 3d => 3d,
            <= 4d => 4d,
            <= 5d => 5d,
            <= 7.5d => 7.5d,
            _ => 10d
        };

        return stepped * magnitude;
    }

    /// <summary>Puts a hairline on a device pixel so it stays crisp.</summary>
    private static double Snap(double value) => Math.Round(value) + 0.5d;
}
