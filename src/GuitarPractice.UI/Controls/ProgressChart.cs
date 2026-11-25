using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;

namespace GuitarPractice.App.Controls;

/// <summary>One value on one day.</summary>
public sealed record ChartPoint(DateOnly Date, double Value, string Tooltip);

/// <summary>
/// A line series. <see cref="Slot"/> (0–3) fixes its colour and marker; it follows the entity, never its rank.
/// <see cref="Target"/>: the exercise's own target, drawn as a thin dashed line in the series colour.
/// </summary>
public sealed record ChartSeries(string Label, int Slot, IReadOnlyList<ChartPoint> Points, double? Target = null);

public enum ChartKind
{
    Line,
    Bars,
}

/// <summary>
/// Categorical palette for chart series (dataviz reference palette, slots 1–4 in fixed order). Validated with the skill's
/// validator (run under Jint, 2026-10-17): adjacent pairs pass CVD and normal-vision floors in light and dark; light
/// slots 3 and 4 are below 3:1 contrast, so lines carry direct labels and the day table is the table view. All-pairs
/// does not pass for any 4th colour, which is why lines (adjacent rule) are capped at four.
/// Each slot also has its own marker shape, so identity never depends on colour alone.
/// </summary>
public static class ChartPalette
{
    private static readonly string[] Light = ["#2a78d6", "#eb6834", "#1baf7a", "#eda100"];
    private static readonly string[] Dark = ["#3987e5", "#d95926", "#199e70", "#c98500"];

    public static Color Series(int slot, bool dark) => Color.Parse((dark ? Dark : Light)[Math.Clamp(slot, 0, Light.Length - 1)]);

    public static Color Surface(bool dark) => Color.Parse(dark ? "#1a1a19" : "#fcfcfb");

    public static Color InkSecondary(bool dark) => Color.Parse(dark ? "#c3c2b7" : "#52514e");

    /// <summary>Circle, square, triangle, diamond for slots 0–3.</summary>
    public static void DrawMarker(DrawingContext context, int slot, Point centre, IBrush fill, IPen? ring, double radius = 5)
    {
        switch (slot)
        {
            case 1:
                context.DrawRectangle(fill, ring, new Rect(centre.X - radius + 0.5, centre.Y - radius + 0.5, 2 * radius - 1, 2 * radius - 1));
                break;
            case 2:
                context.DrawGeometry(fill, ring, Polygon(centre, radius + 1, [(-1, 0.8), (1, 0.8), (0, -1)]));
                break;
            case 3:
                context.DrawGeometry(fill, ring, Polygon(centre, radius + 1, [(0, -1), (1, 0), (0, 1), (-1, 0)]));
                break;
            default:
                context.DrawEllipse(fill, ring, centre, radius, radius);
                break;
        }
    }

    private static StreamGeometry Polygon(Point c, double r, (double X, double Y)[] corners)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        ctx.BeginFigure(new Point(c.X + corners[0].X * r, c.Y + corners[0].Y * r), true);
        foreach (var (x, y) in corners.Skip(1)) ctx.LineTo(new Point(c.X + x * r, c.Y + y * r));
        ctx.EndFigure(true);
        return g;
    }
}

/// <summary>A series' marker in its colour, for lists and legends outside the chart.</summary>
public sealed class SeriesSwatch : Control
{
    public static readonly StyledProperty<int> SlotProperty = AvaloniaProperty.Register<SeriesSwatch, int>(nameof(Slot));

    static SeriesSwatch() => AffectsRender<SeriesSwatch>(SlotProperty);

    public SeriesSwatch()
    {
        Width = 14;
        Height = 14;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual(); // colours differ in light and dark
    }

    public int Slot { get => GetValue(SlotProperty); set => SetValue(SlotProperty, value); }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        ChartPalette.DrawMarker(context, Slot, new Point(Bounds.Width / 2, Bounds.Height / 2), new SolidColorBrush(ChartPalette.Series(Slot, dark)), null);
    }
}

/// <summary>
/// Time chart with one y-axis (never dual): up to <see cref="MaxSeries"/> lines with markers (BPM per exercise) or day bars
/// (minutes), an optional dashed reference line (target BPM), recessive gridlines on nice steps, a legend (≥ 2 series),
/// direct labels at line ends, first/last date labels, and a hover tooltip on the nearest point.
/// </summary>
public sealed class ProgressChart : Control
{
    public const int MaxSeries = 4;

    public static readonly StyledProperty<IReadOnlyList<ChartSeries>?> SeriesProperty =
        AvaloniaProperty.Register<ProgressChart, IReadOnlyList<ChartSeries>?>(nameof(Series));

    public static readonly StyledProperty<IReadOnlyList<ChartPoint>?> PointsProperty =
        AvaloniaProperty.Register<ProgressChart, IReadOnlyList<ChartPoint>?>(nameof(Points));

    public static readonly StyledProperty<ChartKind> KindProperty =
        AvaloniaProperty.Register<ProgressChart, ChartKind>(nameof(Kind));

    public static readonly StyledProperty<double?> ReferenceValueProperty =
        AvaloniaProperty.Register<ProgressChart, double?>(nameof(ReferenceValue));

    public static readonly StyledProperty<string?> ReferenceLabelProperty =
        AvaloniaProperty.Register<ProgressChart, string?>(nameof(ReferenceLabel));

    public static readonly StyledProperty<DateOnly> FromProperty =
        AvaloniaProperty.Register<ProgressChart, DateOnly>(nameof(From));

    public static readonly StyledProperty<DateOnly> ToProperty =
        AvaloniaProperty.Register<ProgressChart, DateOnly>(nameof(To));

    public static readonly StyledProperty<string> EmptyTextProperty =
        AvaloniaProperty.Register<ProgressChart, string>(nameof(EmptyText), "No data yet");

    private const double LeftAxis = 40;
    private const double BottomAxis = 20;
    private const double LegendHeight = 20;
    private const double LabelWidth = 160;

    private (int Series, int Point) _hover = (-1, -1);

    static ProgressChart()
    {
        AffectsRender<ProgressChart>(SeriesProperty, PointsProperty, KindProperty, ReferenceValueProperty, FromProperty, ToProperty);
    }

    public ProgressChart() => ActualThemeVariantChanged += (_, _) => InvalidateVisual(); // redraw when Settings switches the theme

    /// <summary>Line mode: the series to draw (first <see cref="MaxSeries"/> by slot).</summary>
    public IReadOnlyList<ChartSeries>? Series { get => GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }

    /// <summary>Bar mode: one value per day.</summary>
    public IReadOnlyList<ChartPoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }

    public ChartKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public double? ReferenceValue { get => GetValue(ReferenceValueProperty); set => SetValue(ReferenceValueProperty, value); }
    public string? ReferenceLabel { get => GetValue(ReferenceLabelProperty); set => SetValue(ReferenceLabelProperty, value); }
    public DateOnly From { get => GetValue(FromProperty); set => SetValue(FromProperty, value); }
    public DateOnly To { get => GetValue(ToProperty); set => SetValue(ToProperty, value); }
    public string EmptyText { get => GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    private bool IsDark => ActualThemeVariant == ThemeVariant.Dark;
    private Color Grid => Color.Parse(IsDark ? "#33ffffff" : "#1a000000");

    /// <summary>What's drawn: line series (in range, max four) or one bar series.</summary>
    private List<ChartSeries> Visible()
    {
        if (Kind == ChartKind.Bars)
            return [new ChartSeries("", 0, (Points ?? []).Where(InRange).OrderBy(p => p.Date).ToList())];
        return (Series ?? [])
            .Where(s => s.Slot < MaxSeries)
            .OrderBy(s => s.Slot)
            .Select(s => s with { Points = s.Points.Where(InRange).OrderBy(p => p.Date).ToList() })
            .Where(s => s.Points.Count > 0)
            .ToList();
    }

    private bool InRange(ChartPoint p) => p.Date >= From && p.Date <= To;

    private static IEnumerable<double> OwnTargets(List<ChartSeries> series) =>
        series.Where(s => s.Target is not null).Select(s => s.Target!.Value);

    private bool ShowLegend(List<ChartSeries> series) => Kind == ChartKind.Line && series.Count >= 2;

    private Rect PlotFor(List<ChartSeries> series)
    {
        var top = 10 + (ShowLegend(series) ? LegendHeight : 0);
        var right = Kind == ChartKind.Line && series.Count >= 2 ? LabelWidth : 10;
        return new Rect(LeftAxis, top, Math.Max(1, Bounds.Width - LeftAxis - right), Math.Max(1, Bounds.Height - top - BottomAxis));
    }

    private int Days => Math.Max(1, To.DayNumber - From.DayNumber + 1);

    private double XFor(Rect plot, DateOnly date) => plot.X + (date.DayNumber - From.DayNumber + 0.5) * (plot.Width / Days);

    /// <summary>Axis range snapped to a "nice" tick step (1, 2, 2.5 or 5 × 10ⁿ), aiming for about 4 intervals.</summary>
    private (double Min, double Max, double Step) YRange(IEnumerable<double> data)
    {
        var values = data.ToList();
        if (ReferenceValue is double r && Kind == ChartKind.Line) values.Add(r);
        var max = values.Count > 0 ? values.Max() : 1;
        var min = Kind == ChartKind.Bars ? 0 : values.Count > 0 ? values.Min() : 0;
        if (max - min < 1) max = min + 1;

        var raw = (max - min) / 4;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * magnitude).First(s => s >= raw);
        var lo = Kind == ChartKind.Bars ? 0 : Math.Max(0, Math.Floor(min / step) * step);
        var hi = Math.Ceiling(max / step) * step;
        if (hi <= max) hi += step; // headroom so the top mark and the reference label don't touch the edge
        return (lo, hi, step);
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var dark = IsDark;
        var bounds = new Rect(Bounds.Size);
        var surface = ChartPalette.Surface(dark);
        context.FillRectangle(new SolidColorBrush(surface), bounds, 8);
        var ink = new SolidColorBrush(ChartPalette.InkSecondary(dark));
        var series = Visible();

        if (series.Sum(s => s.Points.Count) == 0)
        {
            DrawText(context, EmptyText, new Point(bounds.Width / 2, bounds.Height / 2), ink, center: true);
            return;
        }

        var plot = PlotFor(series);
        var (min, max, step) = YRange(series.SelectMany(s => s.Points).Select(p => p.Value).Concat(OwnTargets(series)));
        double Y(double v) => plot.Bottom - (v - min) / (max - min) * plot.Height;

        // Recessive gridlines + y labels on the nice steps.
        var gridPen = new Pen(new SolidColorBrush(Grid), 1);
        for (var v = min; v <= max + step / 1000; v += step)
        {
            var y = Math.Round(Y(v)) + 0.5;
            context.DrawLine(gridPen, new Point(plot.X, y), new Point(plot.Right, y));
            DrawText(context, v.ToString("0", CultureInfo.InvariantCulture), new Point(plot.X - 6, y), ink, alignRight: true);
        }

        DrawText(context, From.ToString("MMM d", CultureInfo.InvariantCulture), new Point(plot.X, plot.Bottom + 10), ink);
        DrawText(context, To.ToString("MMM d", CultureInfo.InvariantCulture), new Point(plot.Right, plot.Bottom + 10), ink, alignRight: true);

        if (Kind == ChartKind.Bars)
        {
            var color = new SolidColorBrush(ChartPalette.Series(0, dark));
            var width = Math.Max(1, plot.Width / Days - 2); // 2 px surface gap between bars
            foreach (var p in series[0].Points)
            {
                var x = XFor(plot, p.Date) - width / 2;
                var top = Y(p.Value);
                DrawBar(context, color, new Rect(x, top, width, Math.Max(1, plot.Bottom - top)), Math.Min(4, width / 2));
            }
        }
        else
        {
            if (ReferenceValue is double reference)
            {
                var y = Math.Round(Y(reference)) + 0.5;
                context.DrawLine(new Pen(ink, 1, new DashStyle([4, 3], 0)), new Point(plot.X, y), new Point(plot.Right, y));
                if (ReferenceLabel is { } label) DrawText(context, label, new Point(plot.X + 4, y - 8), ink);
            }

            // Own targets: dashed in the series colour, labelled in ink with the series marker (never colour alone).
            var usedLabelYs = new List<double>();
            if (ReferenceValue is double blockRef) usedLabelYs.Add(Y(blockRef) - 8);
            foreach (var s in series.Where(s => s.Target is not null))
            {
                var y = Math.Round(Y(s.Target!.Value)) + 0.5;
                var color = new SolidColorBrush(ChartPalette.Series(s.Slot, dark));
                context.DrawLine(new Pen(color, 1.5, new DashStyle([6, 4], 0)), new Point(plot.X, y), new Point(plot.Right, y));
                var labelY = y - 8;
                if (usedLabelYs.Any(u => Math.Abs(u - labelY) < 12)) continue; // the line is still there; avoid stacked text
                usedLabelYs.Add(labelY);
                ChartPalette.DrawMarker(context, s.Slot, new Point(plot.X + 8, labelY), color, null, 3.5);
                DrawText(context, $"target {s.Target.Value:0}", new Point(plot.X + 16, labelY), ink);
            }

            var ring = new Pen(new SolidColorBrush(surface), 2);
            foreach (var s in series)
            {
                var brush = new SolidColorBrush(ChartPalette.Series(s.Slot, dark));
                if (s.Points.Count > 1)
                {
                    var geometry = new StreamGeometry();
                    using (var g = geometry.Open())
                    {
                        g.BeginFigure(new Point(XFor(plot, s.Points[0].Date), Y(s.Points[0].Value)), false);
                        foreach (var p in s.Points.Skip(1)) g.LineTo(new Point(XFor(plot, p.Date), Y(p.Value)));
                        g.EndFigure(false);
                    }
                    context.DrawGeometry(null, new Pen(brush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
                }
                foreach (var p in s.Points)
                    ChartPalette.DrawMarker(context, s.Slot, new Point(XFor(plot, p.Date), Y(p.Value)), brush, ring);
            }

            if (series.Count >= 2)
            {
                DrawLegend(context, series, ink, dark);
                DrawEndLabels(context, series, plot, Y, ink, dark);
            }
        }

        if (_hover.Series >= 0 && _hover.Series < series.Count && _hover.Point < series[_hover.Series].Points.Count)
        {
            var x = Math.Round(XFor(plot, series[_hover.Series].Points[_hover.Point].Date)) + 0.5;
            context.DrawLine(new Pen(ink, 1), new Point(x, plot.Y), new Point(x, plot.Bottom));
        }
    }

    private void DrawLegend(DrawingContext context, List<ChartSeries> series, IBrush ink, bool dark)
    {
        var x = LeftAxis;
        foreach (var s in series)
        {
            ChartPalette.DrawMarker(context, s.Slot, new Point(x + 6, 17), new SolidColorBrush(ChartPalette.Series(s.Slot, dark)), null, 4.5);
            var text = Fit(s.Label, 260, ink);
            context.DrawText(text, new Point(x + 15, 17 - text.Height / 2));
            x += 15 + text.Width + 16;
            if (x > Bounds.Width - 40) break;
        }
    }

    /// <summary>Direct labels at the right of each line's last point, nudged apart so they never overlap.</summary>
    private static void DrawEndLabels(DrawingContext context, List<ChartSeries> series, Rect plot, Func<double, double> y, IBrush ink, bool dark)
    {
        var labels = series
            .Select(s => (Series: s, Y: y(s.Points[^1].Value), Text: Fit(s.Label, LabelWidth - 22, ink)))
            .OrderBy(l => l.Y)
            .ToList();
        const double gap = 14;
        var placed = new List<double>();
        foreach (var l in labels)
        {
            var at = Math.Max(l.Y, placed.Count > 0 ? placed[^1] + gap : double.MinValue);
            placed.Add(Math.Min(at, plot.Bottom));
        }
        for (var i = 0; i < labels.Count; i++)
        {
            var x = plot.Right + 8;
            ChartPalette.DrawMarker(context, labels[i].Series.Slot, new Point(x + 4, placed[i]),
                new SolidColorBrush(ChartPalette.Series(labels[i].Series.Slot, dark)), null, 3.5);
            context.DrawText(labels[i].Text, new Point(x + 12, placed[i] - labels[i].Text.Height / 2));
        }
    }

    /// <summary>The label, shortened with "…" until it fits <paramref name="maxWidth"/> (measured, not counted).</summary>
    private static FormattedText Fit(string text, double maxWidth, IBrush brush)
    {
        var formatted = Formatted(text, brush);
        for (var n = text.Length - 1; formatted.Width > maxWidth && n > 1; n--)
            formatted = Formatted(text[..n].TrimEnd() + "…", brush);
        return formatted;
    }

    private static FormattedText Formatted(string text, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, brush);

    private static void DrawBar(DrawingContext context, IBrush brush, Rect r, double radius)
    {
        // Rounded data-end (top), square at the baseline.
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(r.X, r.Bottom), true);
            c.LineTo(new Point(r.X, r.Y + radius));
            c.ArcTo(new Point(r.X + radius, r.Y), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            c.LineTo(new Point(r.Right - radius, r.Y));
            c.ArcTo(new Point(r.Right, r.Y + radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            c.LineTo(new Point(r.Right, r.Bottom));
            c.EndFigure(true);
        }
        context.DrawGeometry(brush, null, g);
    }

    private static void DrawText(DrawingContext context, string text, Point at, IBrush brush, bool alignRight = false, bool center = false)
    {
        var formatted = Formatted(text, brush);
        var x = center ? at.X - formatted.Width / 2 : alignRight ? at.X - formatted.Width : at.X;
        context.DrawText(formatted, new Point(x, at.Y - formatted.Height / 2));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        var series = Visible();
        if (series.Count == 0) return;
        var plot = PlotFor(series);
        var (min, max, _) = YRange(series.SelectMany(s => s.Points).Select(p => p.Value).Concat(OwnTargets(series)));
        var pos = e.GetPosition(this);

        var best = (Series: -1, Point: -1, Distance: double.MaxValue);
        for (var s = 0; s < series.Count; s++)
            for (var i = 0; i < series[s].Points.Count; i++)
            {
                var p = series[s].Points[i];
                var px = XFor(plot, p.Date);
                var py = plot.Bottom - (p.Value - min) / (max - min) * plot.Height;
                var d = Kind == ChartKind.Bars ? Math.Abs(px - pos.X) : Math.Sqrt((px - pos.X) * (px - pos.X) + (py - pos.Y) * (py - pos.Y) / 4);
                if (d < best.Distance) best = (s, i, d);
            }
        var hit = best.Distance <= Math.Max(14, plot.Width / Days) ? (best.Series, best.Point) : (-1, -1);
        if (hit == _hover) return;
        _hover = hit;
        ToolTip.SetTip(this, hit.Item1 >= 0 ? series[hit.Item1].Points[hit.Item2].Tooltip : null);
        ToolTip.SetIsOpen(this, hit.Item1 >= 0);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = (-1, -1);
        ToolTip.SetIsOpen(this, false);
        InvalidateVisual();
    }
}
