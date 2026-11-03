using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GuitarPractice.Domain.Rhythm;

namespace GuitarPractice.App.Controls;

/// <summary>One note or rest drawn the way <see cref="RhythmNotationView"/> draws it (the rhythm builder's buttons), so
/// no music font is needed on any OS.</summary>
public sealed class NoteGlyph : Control
{
    public static readonly StyledProperty<NoteLength> LengthProperty =
        AvaloniaProperty.Register<NoteGlyph, NoteLength>(nameof(Length), NoteLength.Quarter);

    public static readonly StyledProperty<bool> IsRestProperty =
        AvaloniaProperty.Register<NoteGlyph, bool>(nameof(IsRest));

    public static readonly StyledProperty<IBrush?> InkProperty =
        AvaloniaProperty.Register<NoteGlyph, IBrush?>(nameof(Ink), Brushes.Gray);

    static NoteGlyph()
    {
        AffectsRender<NoteGlyph>(LengthProperty, IsRestProperty, InkProperty);
    }

    public NoteLength Length
    {
        get => GetValue(LengthProperty);
        set => SetValue(LengthProperty, value);
    }

    public bool IsRest
    {
        get => GetValue(IsRestProperty);
        set => SetValue(IsRestProperty, value);
    }

    public IBrush? Ink
    {
        get => GetValue(InkProperty);
        set => SetValue(InkProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(26, 44);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var ink = Ink ?? Brushes.Gray;
        var note = new RhythmNote(Length, IsRest: IsRest);
        // The notation draws around its staff line; centre that on the glyph (a stemmed note sits lower).
        var x = Bounds.Width / 2 - (IsRest ? 0 : 3);
        var dy = Bounds.Height / 2 + (IsRest || Length == NoteLength.Whole ? 0 : RhythmNotationView.StemLength / 2 - 2);
        using (context.PushTransform(Matrix.CreateTranslation(0, dy - RhythmNotationView.StaffY)))
        {
            if (IsRest)
            {
                // Whole and half rests are the same block: the line shows which hangs below and which sits on it.
                if (Length is NoteLength.Whole or NoteLength.Half)
                    context.DrawLine(new Pen(ink, 1), new Point(x - 10, RhythmNotationView.StaffY), new Point(x + 10, RhythmNotationView.StaffY));
                RhythmNotationView.DrawRest(context, note, x, ink);
                return;
            }
            RhythmNotationView.DrawHead(context, note, x, ink);
            if (Length == NoteLength.Whole) return;
            var stemTop = RhythmNotationView.StaffY - RhythmNotationView.StemLength;
            context.DrawLine(new Pen(ink, 1.3), new Point(x + 5.6, RhythmNotationView.StaffY - 1), new Point(x + 5.6, stemTop));
            if (note.Beams > 0) RhythmNotationView.DrawFlags(context, note.Beams, x + 5.6, stemTop, ink);
        }
    }
}
