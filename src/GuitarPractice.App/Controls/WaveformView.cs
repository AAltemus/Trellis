using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using GuitarPractice.Audio;

namespace GuitarPractice.App.Controls;

/// <summary>
/// Draws a take's waveform with the playhead and the loop region. Click to seek (<see cref="SeekCommand"/> gets the
/// 0..1 position); drag to select a loop (<see cref="LoopStart"/>/<see cref="LoopEnd"/>, two-way, 0..1).
/// </summary>
public sealed class WaveformView : Control
{
    private const double DragThreshold = 4;

    public static readonly StyledProperty<WaveformPeaks?> PeaksProperty =
        AvaloniaProperty.Register<WaveformView, WaveformPeaks?>(nameof(Peaks));

    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(Position));

    public static readonly StyledProperty<double> LoopStartProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(LoopStart), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> LoopEndProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(LoopEnd), 1, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> LoopEnabledProperty =
        AvaloniaProperty.Register<WaveformView, bool>(nameof(LoopEnabled));

    public static readonly StyledProperty<ICommand?> SeekCommandProperty =
        AvaloniaProperty.Register<WaveformView, ICommand?>(nameof(SeekCommand));

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#14808080"));
    private static readonly IPen WavePen = new Pen(new SolidColorBrush(Color.Parse("#60A5FA")), 1);
    private static readonly IPen MidPen = new Pen(new SolidColorBrush(Color.Parse("#40808080")), 1);
    private static readonly IBrush LoopBrush = new SolidColorBrush(Color.Parse("#33FFB300"));
    private static readonly IPen LoopEdgePen = new Pen(new SolidColorBrush(Color.Parse("#FFB300")), 2);
    private static readonly IPen PlayheadPen = new Pen(new SolidColorBrush(Color.Parse("#E53935")), 2);

    private Point? _pressAt;
    private bool _dragging;

    static WaveformView()
    {
        AffectsRender<WaveformView>(PeaksProperty, PositionProperty, LoopStartProperty, LoopEndProperty, LoopEnabledProperty);
    }

    public WaveformPeaks? Peaks { get => GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public double LoopStart { get => GetValue(LoopStartProperty); set => SetValue(LoopStartProperty, value); }
    public double LoopEnd { get => GetValue(LoopEndProperty); set => SetValue(LoopEndProperty, value); }
    public bool LoopEnabled { get => GetValue(LoopEnabledProperty); set => SetValue(LoopEnabledProperty, value); }
    public ICommand? SeekCommand { get => GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var w = Bounds.Width;
        var h = Bounds.Height;
        context.FillRectangle(Background, new Rect(0, 0, w, h), 6);
        var mid = h / 2;
        context.DrawLine(MidPen, new Point(0, mid), new Point(w, mid));

        if (LoopEnabled && LoopEnd > LoopStart)
        {
            var x0 = LoopStart * w;
            var x1 = LoopEnd * w;
            context.FillRectangle(LoopBrush, new Rect(x0, 0, x1 - x0, h));
            context.DrawLine(LoopEdgePen, new Point(x0, 0), new Point(x0, h));
            context.DrawLine(LoopEdgePen, new Point(x1, 0), new Point(x1, h));
        }

        if (Peaks is { Max.Length: > 0 } peaks)
        {
            var columns = peaks.Max.Length;
            var scale = mid * 0.95;
            for (var x = 0; x < (int)w; x++)
            {
                var c = Math.Min(columns - 1, (int)((long)x * columns / Math.Max(1, (int)w)));
                var top = mid - Math.Clamp(peaks.Max[c], -1, 1) * scale;
                var bottom = mid - Math.Clamp(peaks.Min[c], -1, 1) * scale;
                if (bottom - top < 1) bottom = top + 1;
                context.DrawLine(WavePen, new Point(x + 0.5, top), new Point(x + 0.5, bottom));
            }
        }

        var px = Math.Clamp(Position, 0, 1) * w;
        context.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, h));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerPressed(e);
        if (Peaks is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressAt = e.GetPosition(this);
        _dragging = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        if (_pressAt is not { } start) return;
        var now = e.GetPosition(this);
        if (!_dragging && Math.Abs(now.X - start.X) < DragThreshold) return;
        _dragging = true;
        var a = Fraction(start.X);
        var b = Fraction(now.X);
        LoopStart = Math.Min(a, b);
        LoopEnd = Math.Max(a, b);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerReleased(e);
        if (_pressAt is not { } start) return;
        if (!_dragging)
        {
            var fraction = Fraction(start.X);
            if (SeekCommand?.CanExecute(fraction) == true) SeekCommand.Execute(fraction);
        }
        _pressAt = null;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private double Fraction(double x) => Bounds.Width > 0 ? Math.Clamp(x / Bounds.Width, 0, 1) : 0;
}
