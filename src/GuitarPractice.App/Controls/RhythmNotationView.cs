using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GuitarPractice.Domain.Rhythm;

namespace GuitarPractice.App.Controls;

/// <summary>How a played note went, for colouring it in the notation.</summary>
public enum NoteMarkKind
{
    None,
    OnTime,
    Close,
    Missed,
}

/// <summary>A result shown on note <see cref="NoteIndex"/> of the pattern: its colour and a short label ("+12").</summary>
public sealed record NoteMark(int NoteIndex, NoteMarkKind Kind, string Label);

/// <summary>
/// One bar of rhythm as single-line (percussion-style) notation: noteheads, stems, flags, beams grouped by the meter's
/// beats (tuplets beamed on their own), dots, ties, rests and tuplet numbers, with the beats numbered under the line.
/// Notes are spaced by their time (a half note takes twice the room of a quarter), so the picture is also a timeline:
/// <see cref="PlayheadTick"/> draws where the take is. <see cref="Marks"/> colour notes after a take.
/// </summary>
public sealed class RhythmNotationView : Control
{
    public static readonly StyledProperty<RhythmPattern?> PatternProperty =
        AvaloniaProperty.Register<RhythmNotationView, RhythmPattern?>(nameof(Pattern));

    public static readonly StyledProperty<IReadOnlyList<NoteMark>?> MarksProperty =
        AvaloniaProperty.Register<RhythmNotationView, IReadOnlyList<NoteMark>?>(nameof(Marks));

    /// <summary>Tick within the bar to draw the playhead at; negative = none.</summary>
    public static readonly StyledProperty<double> PlayheadTickProperty =
        AvaloniaProperty.Register<RhythmNotationView, double>(nameof(PlayheadTick), -1);

    public static readonly StyledProperty<IBrush?> InkProperty =
        AvaloniaProperty.Register<RhythmNotationView, IBrush?>(nameof(Ink), Brushes.Gray);

    public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
        AvaloniaProperty.Register<RhythmNotationView, IBrush?>(nameof(PlayheadBrush), Brushes.DodgerBlue);

    public static readonly IBrush OnTimeBrush = new SolidColorBrush(Color.Parse("#1a9c6c"));
    public static readonly IBrush CloseBrush = new SolidColorBrush(Color.Parse("#d99a00"));
    public static readonly IBrush MissedBrush = new SolidColorBrush(Color.Parse("#d84a3a"));

    private const double StaffY = 84;
    private const double StemLength = 34;
    private const double BeamGap = 6;
    private const double Margin = 22;

    static RhythmNotationView()
    {
        AffectsRender<RhythmNotationView>(PatternProperty, MarksProperty, PlayheadTickProperty, InkProperty, PlayheadBrushProperty);
    }

    public RhythmPattern? Pattern
    {
        get => GetValue(PatternProperty);
        set => SetValue(PatternProperty, value);
    }

    public IReadOnlyList<NoteMark>? Marks
    {
        get => GetValue(MarksProperty);
        set => SetValue(MarksProperty, value);
    }

    public double PlayheadTick
    {
        get => GetValue(PlayheadTickProperty);
        set => SetValue(PlayheadTickProperty, value);
    }

    public IBrush? Ink
    {
        get => GetValue(InkProperty);
        set => SetValue(InkProperty, value);
    }

    public IBrush? PlayheadBrush
    {
        get => GetValue(PlayheadBrushProperty);
        set => SetValue(PlayheadBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, 128);

    /// <summary>X of a note (or beat) starting at <paramref name="tick"/>; notes sit a little after their start.</summary>
    private double X(double tick, RhythmPattern p) => Margin + 10 + tick / p.Meter.BarTicks * (Bounds.Width - 2 * Margin - 16);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var ink = Ink ?? Brushes.Gray;
        var pen = new Pen(ink, 1.4);
        var width = Bounds.Width;
        context.DrawLine(pen, new Point(Margin - 6, StaffY), new Point(width - Margin + 6, StaffY));
        context.DrawLine(new Pen(ink, 1.6), new Point(Margin - 6, StaffY - 14), new Point(Margin - 6, StaffY + 14));
        context.DrawLine(new Pen(ink, 1.6), new Point(width - Margin + 6, StaffY - 14), new Point(width - Margin + 6, StaffY + 14));
        if (Pattern is not { } p) return;

        // Beat numbers (one per click) under the line.
        var beats = p.Meter.BeatTicks;
        for (var b = 0; b < beats.Count; b++)
            DrawText(context, (b + 1).ToString(CultureInfo.InvariantCulture), X(beats[b], p), StaffY + 30, ink, 12, b == 0);

        var marks = (Marks ?? []).ToDictionary(m => m.NoteIndex);
        var notes = p.Notes;
        var stemTop = StaffY - StemLength;
        var beamGroups = BeamGroups(p);
        var lastLabelX = double.MinValue; // labels closer than this would overlap: the note keeps its colour only

        for (var i = 0; i < notes.Count; i++)
        {
            var n = notes[i];
            var x = X(n.Start, p);
            var mark = marks.GetValueOrDefault(i);
            var brush = mark?.Kind switch
            {
                NoteMarkKind.OnTime => OnTimeBrush,
                NoteMarkKind.Close => CloseBrush,
                NoteMarkKind.Missed => MissedBrush,
                _ => ink,
            };
            if (n.IsRest) DrawRest(context, n, x, ink);
            else
            {
                DrawHead(context, n, x, brush);
                if (n.Length != NoteLength.Whole)
                    context.DrawLine(new Pen(ink, 1.3), new Point(x + 5.6, StaffY - 1), new Point(x + 5.6, stemTop));
                if (n.Beams > 0 && beamGroups[i] < 0) DrawFlags(context, n.Beams, x + 5.6, stemTop, ink);
            }
            for (var d = 0; d < n.Dots; d++)
                context.DrawEllipse(ink, null, new Point(x + 11 + d * 5, n.IsRest ? StaffY - 4 : StaffY - 3), 1.7, 1.7);
            if (n.TiedToNext && i + 1 < notes.Count)
            {
                var x2 = X(notes[i + 1].Start, p);
                var tie = new StreamGeometry();
                using (var g = tie.Open())
                {
                    g.BeginFigure(new Point(x + 3, StaffY + 7), false);
                    g.QuadraticBezierTo(new Point((x + x2) / 2, StaffY + 17), new Point(x2 - 3, StaffY + 7));
                }
                context.DrawGeometry(null, new Pen(ink, 1.3), tie);
            }
            if (mark is { Label.Length: > 0 } && x - lastLabelX >= 30)
            {
                DrawText(context, mark.Label, x, 8, brush, 11, true);
                lastLabelX = x;
            }
        }

        DrawBeams(context, p, beamGroups, stemTop, ink);
        DrawTuplets(context, p, beamGroups, stemTop, ink);

        if (PlayheadTick >= 0 && PlayheadTick <= p.Meter.BarTicks)
        {
            var x = X(PlayheadTick, p) - 4;
            context.DrawLine(new Pen(PlayheadBrush ?? Brushes.DodgerBlue, 2), new Point(x, 22), new Point(x, StaffY + 18));
        }
    }

    /// <summary>
    /// For each note, the beam group it belongs to (an index, shared by 2+ consecutive beamable notes in the same beat or
    /// tuplet), or −1 when it isn't beamed (rests, quarters and longer, a lone 8th).
    /// </summary>
    private static int[] BeamGroups(RhythmPattern p)
    {
        var notes = p.Notes;
        var group = new int[notes.Count];
        Array.Fill(group, -1);
        var next = 0;
        var i = 0;
        while (i < notes.Count)
        {
            if (notes[i].IsRest || notes[i].Beams == 0)
            {
                i++;
                continue;
            }
            var key = Key(p, notes[i]);
            var j = i + 1;
            while (j < notes.Count && !notes[j].IsRest && notes[j].Beams > 0 && Key(p, notes[j]) == key) j++;
            if (j - i >= 2)
            {
                for (var k = i; k < j; k++) group[k] = next;
                next++;
            }
            i = j;
        }
        return group;
    }

    /// <summary>Beam within a tuplet, else within the beat (click group) the note starts in.</summary>
    private static int Key(RhythmPattern p, RhythmNote n) => n.Tuplet is { } t ? 1000 + t.Group : p.Meter.BeatAt(n.Start);

    private void DrawBeams(DrawingContext context, RhythmPattern p, int[] groups, double stemTop, IBrush ink)
    {
        var notes = p.Notes;
        for (var i = 0; i < notes.Count; i++)
        {
            if (groups[i] < 0) continue;
            var stem = X(notes[i].Start, p) + 5.6;
            var nextInGroup = i + 1 < notes.Count && groups[i + 1] == groups[i];
            var prevInGroup = i > 0 && groups[i - 1] == groups[i];
            for (var level = 1; level <= notes[i].Beams; level++)
            {
                var y = stemTop + (level - 1) * BeamGap;
                if (nextInGroup && notes[i + 1].Beams >= level)
                    Beam(context, stem, X(notes[i + 1].Start, p) + 5.6, y, ink);
                else if (!(prevInGroup && notes[i - 1].Beams >= level) && level > 1)
                    Beam(context, nextInGroup ? stem : stem - 8, nextInGroup ? stem + 8 : stem, y, ink); // a stub
            }
        }
    }

    private static void Beam(DrawingContext context, double x1, double x2, double y, IBrush ink) =>
        context.DrawRectangle(ink, null, new Rect(x1 - 0.6, y, x2 - x1 + 1.2, 3.6));

    private void DrawTuplets(DrawingContext context, RhythmPattern p, int[] groups, double stemTop, IBrush ink)
    {
        foreach (var tuplet in p.Notes.Select((n, i) => (n, i)).Where(x => x.n.Tuplet is not null).GroupBy(x => x.n.Tuplet!.Group))
        {
            var first = tuplet.First();
            var last = tuplet.Last();
            var x1 = X(first.n.Start, p);
            var x2 = X(last.n.Start, p) + 6;
            var y = stemTop - 12;
            var mid = (x1 + x2) / 2;
            var beamed = tuplet.All(x => groups[x.i] >= 0 && groups[x.i] == groups[first.i]);
            if (!beamed)
            {
                var pen = new Pen(ink, 1);
                context.DrawLine(pen, new Point(x1, y + 4), new Point(x1, y));
                context.DrawLine(pen, new Point(x1, y), new Point(mid - 7, y));
                context.DrawLine(pen, new Point(mid + 7, y), new Point(x2, y));
                context.DrawLine(pen, new Point(x2, y), new Point(x2, y + 4));
            }
            DrawText(context, first.n.Tuplet!.Actual.ToString(CultureInfo.InvariantCulture), mid, y - 7, ink, 11, true);
        }
    }

    private static void DrawHead(DrawingContext context, RhythmNote n, double x, IBrush brush)
    {
        var hollow = n.Length is NoteLength.Half or NoteLength.Whole;
        using (context.PushTransform(Matrix.CreateRotation(-0.35) * Matrix.CreateTranslation(x, StaffY)))
            context.DrawEllipse(hollow ? null : brush, hollow ? new Pen(brush, 1.8) : null, new Point(0, 0), 6, 4.3);
    }

    private static void DrawFlags(DrawingContext context, int count, double stemX, double stemTop, IBrush ink)
    {
        var pen = new Pen(ink, 1.8);
        for (var f = 0; f < count; f++)
        {
            var y = stemTop + f * BeamGap;
            var flag = new StreamGeometry();
            using (var g = flag.Open())
            {
                g.BeginFigure(new Point(stemX, y), false);
                g.QuadraticBezierTo(new Point(stemX + 9, y + 5), new Point(stemX + 6, y + 13));
            }
            context.DrawGeometry(null, pen, flag);
        }
    }

    private static void DrawRest(DrawingContext context, RhythmNote n, double x, IBrush ink)
    {
        switch (n.Length)
        {
            case NoteLength.Whole:
                context.DrawRectangle(ink, null, new Rect(x - 6, StaffY, 12, 4.5));
                break;
            case NoteLength.Half:
                context.DrawRectangle(ink, null, new Rect(x - 6, StaffY - 4.5, 12, 4.5));
                break;
            case NoteLength.Quarter:
                var zig = new StreamGeometry();
                using (var g = zig.Open())
                {
                    g.BeginFigure(new Point(x - 2, StaffY - 15), false);
                    g.LineTo(new Point(x + 3, StaffY - 9));
                    g.LineTo(new Point(x - 2, StaffY - 3));
                    g.LineTo(new Point(x + 3, StaffY + 3));
                    g.QuadraticBezierTo(new Point(x - 5, StaffY + 1), new Point(x, StaffY + 10));
                }
                context.DrawGeometry(null, new Pen(ink, 2.2), zig);
                break;
            default:
                // 8th, 16th, 32nd: a slanted stem with one, two or three flags (a dot and a hook each).
                var flags = n.Beams;
                var pen = new Pen(ink, 1.6);
                var top = StaffY - 10;
                context.DrawLine(pen, new Point(x + 4, top), new Point(x - 1, StaffY + 6 + (flags - 1) * 5));
                for (var f = 0; f < flags; f++)
                {
                    var y = top + f * 5;
                    var fx = x + 4 - f * 1.2;
                    context.DrawEllipse(ink, null, new Point(fx - 6, y + 1), 2, 2);
                    var hook = new StreamGeometry();
                    using (var g = hook.Open())
                    {
                        g.BeginFigure(new Point(fx - 6, y + 2), false);
                        g.QuadraticBezierTo(new Point(fx - 2, y + 3), new Point(fx, y));
                    }
                    context.DrawGeometry(null, pen, hook);
                }
                break;
        }
    }

    private static void DrawText(DrawingContext context, string text, double centerX, double y, IBrush brush, double size, bool bold)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, brush);
        context.DrawText(formatted, new Point(centerX - formatted.Width / 2, y));
    }
}
