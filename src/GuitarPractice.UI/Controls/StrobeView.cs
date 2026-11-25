using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GuitarPractice.App.Controls;

/// <summary>
/// A strobe tuner display: two striped bands that drift right when the note is sharp, left when flat, and stand still
/// when it's in tune; the further off, the faster. The fine band (top) shows the last few cents, the coarse band (bottom)
/// moves 4× faster for big errors. Animated every frame while a pitch is set; without one the bands stop and dim.
/// </summary>
public sealed class StrobeView : Control
{
    /// <summary>Cents off the target note (+ sharp); null = no note.</summary>
    public static readonly StyledProperty<double?> CentsProperty = AvaloniaProperty.Register<StrobeView, double?>(nameof(Cents));

    public static readonly StyledProperty<IBrush?> StripeBrushProperty =
        AvaloniaProperty.Register<StrobeView, IBrush?>(nameof(StripeBrush), Brushes.Gray);

    public static readonly StyledProperty<IBrush?> InTuneBrushProperty =
        AvaloniaProperty.Register<StrobeView, IBrush?>(nameof(InTuneBrush), Brushes.SeaGreen);

    /// <summary>Within this many cents the stripes turn <see cref="InTuneBrush"/>.</summary>
    public static readonly StyledProperty<double> InTuneCentsProperty =
        AvaloniaProperty.Register<StrobeView, double>(nameof(InTuneCents), 3);

    /// <summary>Fine band speed: pixels per second per cent (coarse band: 4×).</summary>
    public const double PixelsPerSecondPerCent = 2.5;

    private const double FinePeriod = 16;
    private const double CoarsePeriod = 48;
    private double _phase;      // fine-band offset in px; the coarse band uses 4× (wrapped by its own period)
    private TimeSpan? _lastFrame;
    private bool _animating;

    static StrobeView()
    {
        AffectsRender<StrobeView>(CentsProperty, StripeBrushProperty, InTuneBrushProperty);
        CentsProperty.Changed.AddClassHandler<StrobeView>((v, _) => v.EnsureAnimating());
    }

    public double? Cents
    {
        get => GetValue(CentsProperty);
        set => SetValue(CentsProperty, value);
    }

    public IBrush? StripeBrush
    {
        get => GetValue(StripeBrushProperty);
        set => SetValue(StripeBrushProperty, value);
    }

    public IBrush? InTuneBrush
    {
        get => GetValue(InTuneBrushProperty);
        set => SetValue(InTuneBrushProperty, value);
    }

    public double InTuneCents
    {
        get => GetValue(InTuneCentsProperty);
        set => SetValue(InTuneCentsProperty, value);
    }

    /// <summary>Fine-band offset (px) after <paramref name="seconds"/> at <paramref name="cents"/>: + = moved right (sharp).</summary>
    public static double Advance(double phase, double cents, double seconds) =>
        Wrap(phase + cents * PixelsPerSecondPerCent * seconds, FinePeriod * CoarsePeriod);

    private static double Wrap(double value, double period) => ((value % period) + period) % period;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EnsureAnimating();
    }

    private void EnsureAnimating()
    {
        if (_animating || Cents is null || TopLevel.GetTopLevel(this) is not { } top) return;
        _animating = true;
        _lastFrame = null;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan now)
    {
        if (Cents is not double cents || TopLevel.GetTopLevel(this) is not { } top)
        {
            _animating = false;
            InvalidateVisual();
            return;
        }
        if (_lastFrame is TimeSpan last) _phase = Advance(_phase, cents, Math.Min(0.1, (now - last).TotalSeconds));
        _lastFrame = now;
        InvalidateVisual();
        top.RequestAnimationFrame(OnFrame);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var cents = Cents;
        var inTune = cents is double c && Math.Abs(c) < InTuneCents;
        var brush = inTune ? InTuneBrush : StripeBrush;
        using (context.PushClip(new RoundedRect(bounds, 4)))
        using (context.PushOpacity(cents is null ? 0.3 : 1))
        {
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80)), bounds);
            var bandHeight = (bounds.Height - 4) / 2;
            DrawBand(context, brush, new Rect(0, 0, bounds.Width, bandHeight), FinePeriod, Wrap(_phase, FinePeriod));
            DrawBand(context, brush, new Rect(0, bandHeight + 4, bounds.Width, bandHeight), CoarsePeriod, Wrap(_phase * 4, CoarsePeriod));
        }
    }

    private static void DrawBand(DrawingContext context, IBrush? brush, Rect band, double period, double offset)
    {
        if (brush is null) return;
        var stripe = period / 2;
        for (var x = offset - period; x < band.Width; x += period)
        {
            var left = Math.Max(0, x);
            var right = Math.Min(band.Width, x + stripe);
            if (right > left) context.FillRectangle(brush, new Rect(band.X + left, band.Y, right - left, band.Height));
        }
    }
}
