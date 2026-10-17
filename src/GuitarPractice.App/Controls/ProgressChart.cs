using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;

namespace GuitarPractice.App.Controls;

/// <summary>One value on one day.</summary>
public sealed record ChartPoint(DateOnly Date, double Value, string Tooltip);

public enum ChartKind
{
    Line,
    Bars,
}

/// <summary>
/// Single-series time chart (one y-axis; never dual): a line with markers (BPM) or day bars (minutes), an optional
/// dashed reference line (target BPM), recessive gridlines, first/last date labels and a hover tooltip on the nearest
/// day. Colours follow the dataviz reference palette, slot 1, with separate light/dark steps
/// (contrast 4.3:1 light / 4.8:1 dark on the chart surfaces).
/// </summary>
public sealed class ProgressChart : Control
{
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
    private const double TopPad = 10;
    private const double RightPad = 10;

    private int _hoverIndex = -1;

    static ProgressChart()
    {
        AffectsRender<ProgressChart>(PointsProperty, KindProperty, ReferenceValueProperty, FromProperty, ToProperty);
    }

    public IReadOnlyList<ChartPoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public ChartKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public double? ReferenceValue { get => GetValue(ReferenceValueProperty); set => SetValue(ReferenceValueProperty, value); }
    public string? ReferenceLabel { get => GetValue(ReferenceLabelProperty); set => SetValue(ReferenceLabelProperty, value); }
    public DateOnly From { get => GetValue(FromProperty); set => SetValue(FromProperty, value); }
    public DateOnly To { get => GetValue(ToProperty); set => SetValue(ToProperty, value); }
    public string EmptyText { get => GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    private bool IsDark => ActualThemeVariant == ThemeVariant.Dark;
    private Color Surface => Color.Parse(IsDark ? "#1a1a19" : "#fcfcfb");
    private Color Series => Color.Parse(IsDark ? "#3987e5" : "#2a78d6");
    private Color InkSecondary => Color.Parse(IsDark ? "#c3c2b7" : "#52514e");
    private Color Grid => Color.Parse(IsDark ? "#33ffffff" : "#1a000000");

    private Rect Plot => new(LeftAxis, TopPad, Math.Max(1, Bounds.Width - LeftAxis - RightPad), Math.Max(1, Bounds.Height - TopPad - BottomAxis));

    private int Days => Math.Max(1, To.DayNumber - From.DayNumber + 1);

    private double XFor(DateOnly date)
    {
        var plot = Plot;
        var slot = plot.Width / Days;
        return plot.X + (date.DayNumber - From.DayNumber + 0.5) * slot;
    }

    /// <summary>Axis range snapped to a "nice" tick step (1, 2, 2.5 or 5 × 10ⁿ), aiming for about 4 intervals.</summary>
    private (double Min, double Max, double Step) YRange(IReadOnlyList<ChartPoint> points)
    {
        var values = points.Select(p => p.Value).ToList();
        if (ReferenceValue is double r) values.Add(r);
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
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(Surface), bounds, 8);
        var plot = Plot;
        var ink = new SolidColorBrush(InkSecondary);
        var points = (Points ?? []).Where(p => p.Date >= From && p.Date <= To).OrderBy(p => p.Date).ToList();

        if (points.Count == 0)
        {
            DrawText(context, EmptyText, new Point(bounds.Width / 2, bounds.Height / 2), ink, center: true);
            return;
        }

        var (min, max, step) = YRange(points);
        double Y(double v) => plot.Bottom - (v - min) / (max - min) * plot.Height;

        // Recessive gridlines + y labels on the nice steps.
        var gridPen = new Pen(new SolidColorBrush(Grid), 1);
        for (var v = min; v <= max + step / 1000; v += step)
        {
            var y = Math.Round(Y(v)) + 0.5;
            context.DrawLine(gridPen, new Point(plot.X, y), new Point(plot.Right, y));
            DrawText(context, v.ToString("0", CultureInfo.InvariantCulture), new Point(plot.X - 6, y), ink, alignRight: true);
        }

        // First / last date labels.
        DrawText(context, From.ToString("MMM d", CultureInfo.InvariantCulture), new Point(plot.X, plot.Bottom + 10), ink);
        DrawText(context, To.ToString("MMM d", CultureInfo.InvariantCulture), new Point(plot.Right, plot.Bottom + 10), ink, alignRight: true);

        var series = new SolidColorBrush(Series);
        if (Kind == ChartKind.Bars)
        {
            var slot = plot.Width / Days;
            var width = Math.Max(1, slot - 2); // 2 px surface gap between bars
            foreach (var p in points)
            {
                var x = XFor(p.Date) - width / 2;
                var top = Y(p.Value);
                DrawBar(context, series, new Rect(x, top, width, Math.Max(1, plot.Bottom - top)), Math.Min(4, width / 2));
            }
        }
        else
        {
            if (points.Count > 1)
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(new Point(XFor(points[0].Date), Y(points[0].Value)), false);
                    foreach (var p in points.Skip(1)) g.LineTo(new Point(XFor(p.Date), Y(p.Value)));
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, new Pen(series, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
            }
            var ring = new Pen(new SolidColorBrush(Surface), 2);
            foreach (var p in points)
                context.DrawEllipse(series, ring, new Point(XFor(p.Date), Y(p.Value)), 5, 5); // 8 px marker + 2 px ring
        }

        if (ReferenceValue is double reference)
        {
            var y = Math.Round(Y(reference)) + 0.5;
            var dashed = new Pen(ink, 1, new DashStyle([4, 3], 0));
            context.DrawLine(dashed, new Point(plot.X, y), new Point(plot.Right, y));
            if (ReferenceLabel is { } label) DrawText(context, label, new Point(plot.Right - 4, y - 8), ink, alignRight: true);
        }

        // Hover: crosshair on the nearest day.
        if (_hoverIndex >= 0 && _hoverIndex < points.Count)
        {
            var x = Math.Round(XFor(points[_hoverIndex].Date)) + 0.5;
            context.DrawLine(new Pen(ink, 1), new Point(x, plot.Y), new Point(x, plot.Bottom));
        }
    }

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
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, brush);
        var x = center ? at.X - formatted.Width / 2 : alignRight ? at.X - formatted.Width : at.X;
        context.DrawText(formatted, new Point(x, at.Y - formatted.Height / 2));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        var points = (Points ?? []).Where(p => p.Date >= From && p.Date <= To).OrderBy(p => p.Date).ToList();
        var x = e.GetPosition(this).X;
        var index = -1;
        var best = double.MaxValue;
        for (var i = 0; i < points.Count; i++)
        {
            var d = Math.Abs(XFor(points[i].Date) - x);
            if (d < best)
            {
                best = d;
                index = i;
            }
        }
        var slot = Plot.Width / Days;
        if (best > Math.Max(12, slot)) index = -1; // hit target wider than the mark, but not the whole chart
        if (index == _hoverIndex) return;
        _hoverIndex = index;
        ToolTip.SetTip(this, index >= 0 ? points[index].Tooltip : null);
        ToolTip.SetIsOpen(this, index >= 0);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverIndex = -1;
        ToolTip.SetIsOpen(this, false);
        InvalidateVisual();
    }
}
