using System.Globalization;
using System.Windows;
using System.Windows.Media;
using costats.Core.Analytics;

namespace costats.App.Controls;

/// <summary>One provider's line on the chart.</summary>
/// <param name="Provider">Picks the accent the line is drawn in.</param>
/// <param name="Values">
/// One value per point of <see cref="UsageChartData.Labels"/>, same order and length.
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

    /// <summary>
    /// The X axis label of every point (a day or an hour of the selected
    /// range), ascending, gaps included.
    /// </summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>One entry per provider that has data in the range.</summary>
    public IReadOnlyList<UsageChartSeries> Series { get; init; } = [];

    /// <summary>
    /// The comparison period, aligned point for point with
    /// <see cref="Labels"/>, drawn as dashed lines. A NaN value is a point the
    /// comparison period does not have, drawn as a gap rather than as zero.
    /// </summary>
    public IReadOnlyList<UsageChartSeries> ComparisonSeries { get; init; } = [];

    /// <summary>
    /// The comparison period's own label for every point, drawn as a second
    /// axis row so the dashed line can be read against its real dates. Empty
    /// entries are points the comparison does not have. Empty when the
    /// comparison has no dates of its own (an average day).
    /// </summary>
    public IReadOnlyList<string> ComparisonLabels { get; init; } = [];

    /// <summary>The current period's point that is still in progress, or null.</summary>
    public int? PartialIndex { get; init; }

    /// <summary>The comparison's point that is still in progress, or null.</summary>
    public int? ComparisonPartialIndex { get; init; }

    /// <summary>Formats a Y axis tick, so the same control serves dollars and tokens.</summary>
    public Func<double, string> AxisLabel { get; init; } = value => value.ToString("0", CultureInfo.InvariantCulture);
}

/// <summary>
/// Pure layout rules of <see cref="UsageChart"/>, kept apart from drawing so
/// they can be checked without rendering.
/// </summary>
public static class UsageChartLayout
{
    /// <summary>Smallest gap kept between two labels in one row.</summary>
    public const double LabelGap = 8d;

    /// <summary>Space kept between the "so far" text and its ring.</summary>
    public const double SoFarGap = 6d;

    /// <summary>
    /// Picks which labels of one axis row to draw and where. The first and
    /// the last real label always come first, so a row never loses its end
    /// date, even when a shorter comparison stops early; the middle one is
    /// added only when it fits. A label that would touch an earlier one is
    /// left out rather than drawn on top of it.
    /// </summary>
    /// <param name="labels">One label per point; empty where the row has none.</param>
    /// <param name="measure">The drawn width of a label.</param>
    /// <param name="left">X of the first point.</param>
    /// <param name="right">X of the last point.</param>
    /// <returns>Point index and left edge of every label to draw.</returns>
    public static IReadOnlyList<(int Index, double X)> PlaceLabels(
        IReadOnlyList<string> labels,
        Func<string, double> measure,
        double left,
        double right)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(measure);

        var count = labels.Count;
        if (count == 0)
        {
            return [];
        }

        var step = count > 1 ? (right - left) / (count - 1) : 0d;
        var candidates = new List<int>(3);
        var first = Enumerable.Range(0, count).FirstOrDefault(index => labels[index].Length > 0, -1);
        var last = Enumerable.Range(0, count).LastOrDefault(index => labels[index].Length > 0, -1);
        if (first < 0)
        {
            return [];
        }

        candidates.Add(first);
        if (last != first)
        {
            candidates.Add(last);
        }

        var middle = count / 2;
        if (count > 2 && middle != first && middle != last && labels[middle].Length > 0)
        {
            candidates.Add(middle);
        }

        var placed = new List<(int Index, double X, double Width)>(candidates.Count);
        foreach (var index in candidates)
        {
            var width = measure(labels[index]);
            // One point keeps its label at the left edge, as the chart always did.
            var anchor = left + (step * index);
            var x = index == 0
                ? anchor
                : index == count - 1
                    ? anchor - width
                    : anchor - (width / 2d);
            x = Math.Clamp(x, left, Math.Max(left, right - width));

            if (placed.Any(other => x < other.X + other.Width + LabelGap && other.X < x + width + LabelGap))
            {
                continue;
            }

            placed.Add((index, x, width));
        }

        return placed
            .OrderBy(entry => entry.X)
            .Select(entry => (entry.Index, entry.X))
            .ToList();
    }

    /// <summary>
    /// The top of the "so far" text for the rings stacked at one point:
    /// above all of them when there is room under the plot's top, below all
    /// of them otherwise, so it never sits on any ring.
    /// </summary>
    public static double SoFarTop(IReadOnlyList<double> ringYs, double textHeight, double plotTop)
    {
        ArgumentNullException.ThrowIfNull(ringYs);
        if (ringYs.Count == 0)
        {
            return plotTop;
        }

        var above = ringYs.Min() - RingRadius - textHeight - SoFarGap;
        return above >= plotTop ? above : ringYs.Max() + RingRadius + SoFarGap;
    }

    /// <summary>Radius of the ring that marks a point still in progress.</summary>
    public const double RingRadius = 3.5d;

    /// <summary>
    /// The left edge of a text centred on <paramref name="x"/>, kept inside
    /// the plot when it fits, and at the plot's left edge when it does not.
    /// </summary>
    public static double ClampedLeft(double x, double width, double left, double right) =>
        Math.Clamp(x - (width / 2d), left, Math.Max(left, right - width));
}

/// <summary>
/// The cost/token chart: a smoothed area series per provider over a
/// continuous day or hour axis, drawn directly with <see cref="DrawingContext"/>.
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

    /// <summary>Extra height for the comparison's own row of dates.</summary>
    private const double ComparisonRowHeight = 15d;

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
        if (width <= AxisGutter + 24 || height <= DateGutter + 40 || data is null || data.Labels.Count == 0)
        {
            return;
        }

        // A second label row carries the comparison's own dates.
        var hasComparisonLabels = data.ComparisonLabels.Any(label => label.Length > 0);
        var left = AxisGutter;
        var right = width - 6d;
        var top = TopPadding;
        var bottom = height - DateGutter - (hasComparisonLabels ? ComparisonRowHeight : 0d);
        if (right <= left || bottom <= top)
        {
            return;
        }

        var peak = 0d;
        foreach (var series in data.Series.Concat(data.ComparisonSeries))
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

        // Axis labels: first, middle, last. Anything denser is unreadable at
        // 90 days and adds nothing at 7. With a comparison, its own dates sit
        // in a second row, and the rows are marked A and B in the gutter.
        DrawLabelRow(drawingContext, data.Labels, left, right, bottom, typeface, dpi);
        if (hasComparisonLabels)
        {
            DrawLabelRow(drawingContext, data.ComparisonLabels, left, right, bottom + ComparisonRowHeight, typeface, dpi);
            DrawRowMarker(drawingContext, "A", left, bottom, typeface, dpi);
            DrawRowMarker(drawingContext, "B", left, bottom + ComparisonRowHeight, typeface, dpi);
        }

        // The comparison goes underneath, so the current period's fill and
        // stroke stay on top where the two cross.
        foreach (var series in data.ComparisonSeries)
        {
            DrawComparison(drawingContext, series, data.Labels.Count, left, right, top, bottom, axisMax);
        }

        foreach (var series in data.Series)
        {
            DrawSeries(drawingContext, series, data.Labels.Count, left, right, top, bottom, axisMax);
        }

        // A point still in progress is a hollow ring with "so far", so a low
        // last value reads as unfinished rather than as a drop.
        DrawPartial(drawingContext, data.ComparisonSeries, data.ComparisonPartialIndex, data.Labels.Count, left, right, top, bottom, axisMax, typeface, dpi);
        DrawPartial(drawingContext, data.Series, data.PartialIndex, data.Labels.Count, left, right, top, bottom, axisMax, typeface, dpi);
    }

    private void DrawPartial(
        DrawingContext drawingContext,
        IReadOnlyList<UsageChartSeries> seriesList,
        int? partialIndex,
        int pointCount,
        double left,
        double right,
        double top,
        double bottom,
        double axisMax,
        Typeface typeface,
        double dpi)
    {
        // -1 means everything is still to come: nothing to mark.
        if (partialIndex is not { } index || index < 0 || index >= pointCount || axisMax <= 0d)
        {
            return;
        }

        var step = pointCount > 1 ? (right - left) / (pointCount - 1) : 0d;
        var x = pointCount > 1 ? left + (step * index) : (left + right) / 2d;
        var rings = new List<double>(seriesList.Count);
        foreach (var series in seriesList)
        {
            if (index >= series.Values.Count || !double.IsFinite(series.Values[index]))
            {
                continue;
            }

            var y = bottom - ((bottom - top) * Math.Min(1d, Math.Max(0d, series.Values[index]) / axisMax));
            var accent = series.Provider == UsageProviderKind.Claude ? ClaudeBrush : CodexBrush;
            var ring = new Pen(accent, 1.6d);
            ring.Freeze();
            drawingContext.DrawEllipse(null, ring, new Point(x, y), UsageChartLayout.RingRadius, UsageChartLayout.RingRadius);
            rings.Add(y);
        }

        if (rings.Count > 0)
        {
            var text = Text("so far", typeface, dpi, LabelBrush);
            var textX = UsageChartLayout.ClampedLeft(x, text.Width, left, right);
            drawingContext.DrawText(text, new Point(textX, UsageChartLayout.SoFarTop(rings, text.Height, top)));
        }
    }

    private void DrawLabelRow(
        DrawingContext drawingContext,
        IReadOnlyList<string> labels,
        double left,
        double right,
        double rowTop,
        Typeface typeface,
        double dpi)
    {
        var placed = UsageChartLayout.PlaceLabels(
            labels,
            label => Text(label, typeface, dpi, LabelBrush).Width,
            left,
            right);

        foreach (var (index, x) in placed)
        {
            drawingContext.DrawText(Text(labels[index], typeface, dpi, LabelBrush), new Point(x, rowTop + 4d));
        }
    }

    private void DrawRowMarker(DrawingContext drawingContext, string marker, double left, double rowTop, Typeface typeface, double dpi)
    {
        var text = Text(marker, typeface, dpi, LabelBrush);
        drawingContext.DrawText(text, new Point(left - 8d - text.Width, rowTop + 4d));
    }

    /// <summary>
    /// Draws one comparison series as a dashed, unfilled line. Each unbroken
    /// run of real values is its own curve, so a missing point is a gap.
    /// </summary>
    private void DrawComparison(
        DrawingContext drawingContext,
        UsageChartSeries series,
        int pointCount,
        double left,
        double right,
        double top,
        double bottom,
        double axisMax)
    {
        if (axisMax <= 0d)
        {
            return;
        }

        var accent = series.Provider == UsageProviderKind.Claude ? ClaudeBrush : CodexBrush;
        var stroke = accent.Clone();
        stroke.Opacity = 0.8;
        stroke.Freeze();
        var pen = new Pen(stroke, 1.4d)
        {
            DashStyle = new DashStyle([4d, 3d], 0d),
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();

        var step = pointCount > 1 ? (right - left) / (pointCount - 1) : 0d;
        var run = new List<Point>();
        var count = Math.Min(series.Values.Count, pointCount);
        for (var index = 0; index <= count; index++)
        {
            var value = index < count ? series.Values[index] : double.NaN;
            if (double.IsFinite(value))
            {
                var x = pointCount > 1 ? left + (step * index) : (left + right) / 2d;
                var y = bottom - ((bottom - top) * Math.Min(1d, Math.Max(0d, value) / axisMax));
                run.Add(new Point(x, y));
                continue;
            }

            if (run.Count == 1)
            {
                drawingContext.DrawEllipse(stroke, null, run[0], 1.6d, 1.6d);
            }
            else if (run.Count > 1)
            {
                var line = new StreamGeometry();
                using (var context = line.Open())
                {
                    context.BeginFigure(run[0], isFilled: false, isClosed: false);
                    AppendSmoothCurve(context, run, top, bottom);
                }

                line.Freeze();
                drawingContext.DrawGeometry(null, pen, line);
            }

            run.Clear();
        }
    }

    private void DrawSeries(
        DrawingContext drawingContext,
        UsageChartSeries series,
        int pointCount,
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

        // Positions come from the label count, so a series is never drawn past
        // the axis even if it carries more values than there are labels.
        // The line stops at the first missing point: everything after it is
        // still in the future.
        var step = pointCount > 1 ? (right - left) / (pointCount - 1) : 0d;
        var count = Math.Min(series.Values.Count, pointCount);
        for (var index = 0; index < count; index++)
        {
            if (!double.IsFinite(series.Values[index]))
            {
                count = index;
                break;
            }
        }

        if (count == 0)
        {
            return;
        }

        var points = new List<Point>(count);
        for (var index = 0; index < count; index++)
        {
            var value = Math.Max(0d, series.Values[index]);
            var x = pointCount > 1 ? left + (step * index) : (left + right) / 2d;
            var y = bottom - ((bottom - top) * Math.Min(1d, value / axisMax));
            points.Add(new Point(x, y));
        }

        var accent = series.Provider == UsageProviderKind.Claude ? ClaudeBrush : CodexBrush;
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
