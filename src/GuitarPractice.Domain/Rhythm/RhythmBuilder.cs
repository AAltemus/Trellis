namespace GuitarPractice.Domain.Rhythm;

/// <summary>
/// Writes a rhythm a click at a time (the Rhythm Trainer's builder): notes, rests and triplets go on the end, a note
/// that wouldn't fit in what's left of the bar is refused (so bars always add up), and a full bar starts the next one,
/// up to <see cref="RhythmPattern.MaxBars"/>. <see cref="Text"/> is the same syntax <see cref="RhythmPattern.Parse"/>
/// reads, so the builder and the text box stay in step.
/// </summary>
public sealed class RhythmBuilder
{
    /// <summary>One click's worth: a note or rest, or a whole tuplet (<see cref="Tuplet"/> notes in brackets).</summary>
    private sealed record Entry(List<string> Tokens, int Tuplet, int Ticks)
    {
        public string Text => Tuplet == 0 ? Tokens[0] : $"[{Tuplet} {string.Join(' ', Tokens)}]";
    }

    private readonly List<Entry> _entries = [];

    public RhythmBuilder(Meter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);
        Meter = meter;
    }

    public Meter Meter { get; }

    /// <summary>A builder holding <paramref name="pattern"/>'s notes (to change a copy of it).</summary>
    public static RhythmBuilder From(RhythmPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var builder = new RhythmBuilder(pattern.Meter);
        var notes = pattern.Notes;
        for (var i = 0; i < notes.Count;)
        {
            if (notes[i].Tuplet is { } t)
            {
                var tokens = new List<string>();
                var ticks = 0;
                for (; i < notes.Count && notes[i].Tuplet?.Group == t.Group; i++)
                {
                    tokens.Add(Token(notes[i]));
                    ticks += notes[i].Ticks;
                }
                builder._entries.Add(new Entry(tokens, t.Actual, ticks));
            }
            else
            {
                builder._entries.Add(new Entry([Token(notes[i])], 0, notes[i].Ticks));
                i++;
            }
        }
        return builder;
    }

    public bool IsEmpty => _entries.Count == 0;

    /// <summary>Ticks written so far.</summary>
    public int Filled => _entries.Sum(e => e.Ticks);

    /// <summary>The bars written so far (a full bar counts; the next one starts with its first note).</summary>
    public int Bars => Math.Max(1, (Filled + Meter.BarTicks - 1) / Meter.BarTicks);

    /// <summary>Whether every bar written so far is full (and there's at least one note): ready to save.</summary>
    public bool IsComplete => Filled > 0 && Filled % Meter.BarTicks == 0;

    /// <summary>The bar the next note goes in (0-based); <see cref="RhythmPattern.MaxBars"/> when all are full.</summary>
    public int CurrentBar => Filled / Meter.BarTicks;

    /// <summary>Ticks left in the bar the next note goes in (0 when all <see cref="RhythmPattern.MaxBars"/> are full).</summary>
    public int LeftInBar => CurrentBar >= RhythmPattern.MaxBars ? 0 : Meter.BarTicks - Filled % Meter.BarTicks;

    /// <summary>Ticks used in the bar the next note goes in.</summary>
    public int UsedInBar => CurrentBar >= RhythmPattern.MaxBars ? Meter.BarTicks : Filled % Meter.BarTicks;

    /// <summary>The written value of a note or rest (<paramref name="triplet"/>: a group of three in the time of two).</summary>
    public static int TicksOf(NoteLength length, bool dotted, bool triplet)
    {
        var ticks = new RhythmNote(length, dotted ? 1 : 0).ComputeTicks();
        return triplet ? ticks * 2 : ticks;
    }

    public bool CanAdd(NoteLength length, bool dotted, bool triplet) =>
        !(dotted && triplet) && TicksOf(length, dotted, triplet) <= LeftInBar;

    /// <summary>Adds a note or rest; a <paramref name="triplet"/> adds three of them in the time of two. False (nothing
    /// added) when it doesn't fit in the bar.</summary>
    public bool Add(NoteLength length, bool dotted = false, bool rest = false, bool triplet = false)
    {
        if (!CanAdd(length, dotted, triplet)) return false;
        var token = $"{(rest ? "r" : "")}{Letter(length)}{(dotted ? "." : "")}";
        _entries.Add(triplet
            ? new Entry([token, token, token], 3, TicksOf(length, false, true))
            : new Entry([token], 0, TicksOf(length, dotted, false)));
        return true;
    }

    /// <summary>The last thing written is a note (not a rest), so it can be tied to the next.</summary>
    public bool CanTie => _entries.Count > 0 && !_entries[^1].Tokens[^1].StartsWith('r');

    public bool IsLastTied => _entries.Count > 0 && _entries[^1].Tokens[^1].EndsWith('~');

    /// <summary>Ties the last note to the next one, or unties it.</summary>
    public void ToggleTie()
    {
        if (!CanTie) return;
        var tokens = _entries[^1].Tokens;
        tokens[^1] = IsLastTied ? tokens[^1][..^1] : tokens[^1] + "~";
    }

    /// <summary>Removes the last note, rest or tuplet.</summary>
    public void Undo()
    {
        if (_entries.Count > 0) _entries.RemoveAt(_entries.Count - 1);
    }

    public void Clear() => _entries.Clear();

    /// <summary>The rhythm as text, with <c>|</c> between full bars.</summary>
    public string Text => Write(openNextBar: false);

    /// <summary>What's written so far as a pattern to draw, with the next bar shown empty once a bar is full.</summary>
    public RhythmPattern Preview =>
        RhythmPattern.TryParseDraft(Meter, Write(openNextBar: CurrentBar is > 0 and < RhythmPattern.MaxBars && Filled % Meter.BarTicks == 0),
            out var pattern, out _) ? pattern! : throw new InvalidOperationException("The builder wrote a rhythm it can't read.");

    private string Write(bool openNextBar)
    {
        var parts = new List<string>();
        var at = 0;
        foreach (var e in _entries)
        {
            if (at > 0 && at % Meter.BarTicks == 0) parts.Add("|");
            parts.Add(e.Text);
            at += e.Ticks;
        }
        if (openNextBar) parts.Add("|");
        return string.Join(' ', parts);
    }

    private static string Letter(NoteLength length) => length switch
    {
        NoteLength.Whole => "w",
        NoteLength.Half => "h",
        NoteLength.Quarter => "q",
        NoteLength.Eighth => "e",
        NoteLength.Sixteenth => "s",
        _ => "t",
    };

    private static string Token(RhythmNote n) =>
        $"{(n.IsRest ? "r" : "")}{Letter(n.Length)}{new string('.', n.Dots)}{(n.TiedToNext ? "~" : "")}";
}
