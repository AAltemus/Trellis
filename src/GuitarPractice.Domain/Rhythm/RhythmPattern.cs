using System.Globalization;

namespace GuitarPractice.Domain.Rhythm;

/// <summary>A written note value; the number is its denominator (a quarter = 4).</summary>
public enum NoteLength
{
    Whole = 1,
    Half = 2,
    Quarter = 4,
    Eighth = 8,
    Sixteenth = 16,
    ThirtySecond = 32,
}

/// <summary><see cref="Actual"/> notes in the time of <see cref="Normal"/> (a triplet is 3:2); notes with the same
/// <see cref="Group"/> number are bracketed together.</summary>
public sealed record Tuplet(int Actual, int Normal, int Group);

/// <summary>
/// One written symbol of a rhythm: a note or rest with its value, dots, tie to the next note and tuplet.
/// <see cref="Start"/> and <see cref="Ticks"/> are filled in by <see cref="RhythmPattern"/>.
/// </summary>
public sealed record RhythmNote(NoteLength Length, int Dots = 0, bool IsRest = false, bool TiedToNext = false, Tuplet? Tuplet = null)
{
    public int Start { get; init; }

    public int Ticks { get; init; }

    /// <summary>Beams (8th = 1 … 32nd = 3); 0 for quarters and longer.</summary>
    public int Beams => Length switch
    {
        NoteLength.Eighth => 1,
        NoteLength.Sixteenth => 2,
        NoteLength.ThirtySecond => 3,
        _ => 0,
    };

    /// <summary>The written value in ticks (dots and tuplet included).</summary>
    public int ComputeTicks()
    {
        var baseTicks = Meter.TicksPerQuarter * 4 / (int)Length;
        var ticks = baseTicks;
        var add = baseTicks;
        for (var d = 0; d < Dots; d++)
        {
            add /= 2;
            ticks += add;
        }
        return Tuplet is { } t ? ticks * t.Normal / t.Actual : ticks;
    }
}

/// <summary>
/// One or more bars of rhythm in a meter. The notes you play are the <see cref="Onsets"/>: every note that isn't a rest
/// and isn't the continuation of a tie. Built from a compact text (see <see cref="Parse"/>); the notes must fill each bar
/// exactly.
/// </summary>
public sealed class RhythmPattern
{
    public const int MaxBars = 4;

    private RhythmPattern(string name, int level, Meter meter, IReadOnlyList<RhythmNote> notes, int bars, string text)
    {
        Name = name;
        Level = level;
        Meter = meter;
        Notes = notes;
        Bars = bars;
        Text = text;
        var onsets = new List<int>();
        var tiedIn = false;
        foreach (var n in notes)
        {
            if (!n.IsRest && !tiedIn) onsets.Add(n.Start);
            tiedIn = !n.IsRest && n.TiedToNext;
        }
        Onsets = onsets;
    }

    public string Name { get; }

    /// <summary>1 (basic) … 5 (hardest); 0 for your own.</summary>
    public int Level { get; }

    public Meter Meter { get; }

    public IReadOnlyList<RhythmNote> Notes { get; }

    /// <summary>How many bars the rhythm spans (1–<see cref="MaxBars"/>).</summary>
    public int Bars { get; }

    /// <summary>The whole rhythm in ticks (<see cref="Bars"/> bars).</summary>
    public int Length => Bars * Meter.BarTicks;

    /// <summary>The text it was written as (<see cref="Parse"/>).</summary>
    public string Text { get; }

    /// <summary>Ticks (from the first bar's start) of the notes to play, in order.</summary>
    public IReadOnlyList<int> Onsets { get; }

    /// <summary>Which note in <see cref="Notes"/> each onset is.</summary>
    public int NoteIndexOfOnset(int onset)
    {
        var tick = Onsets[onset];
        for (var i = 0; i < Notes.Count; i++)
            if (Notes[i].Start == tick && !Notes[i].IsRest) return i;
        return -1;
    }

    public override string ToString() => $"{Name} ({Meter.Signature})";
    /// <summary>
    /// Space-separated symbols: <c>w h q e s t</c> = whole, half, quarter, 8th, 16th, 32nd; a trailing <c>.</c> per
    /// dot; an <c>r</c> in front for a rest (<c>rq</c>); a trailing <c>~</c> ties to the next note (<c>e~</c>, also
    /// across a bar line); <c>[3 e e e]</c> for a tuplet (3, 5, 6 or 7 notes in the time of 2, 4, 4 or 4); and <c>|</c>
    /// between bars (up to <see cref="MaxBars"/>). Example (4/4): <c>q e. s [3 e e e] q | h q q</c>. Throws
    /// <see cref="FormatException"/> on bad input or when a bar isn't filled exactly (for text being typed, use
    /// <see cref="TryParse"/>, which doesn't throw).
    /// </summary>
    public static RhythmPattern Parse(string name, int level, Meter meter, string text) =>
        TryParse(name, level, meter, text, out var pattern, out var error) ? pattern! : throw new FormatException($"{name}: {error}");

    /// <summary>As <see cref="Parse"/>, but says what's wrong in <paramref name="error"/> ("bar 2's notes add up to …")
    /// instead of throwing.</summary>
    public static bool TryParse(string name, int level, Meter meter, string text, out RhythmPattern? pattern, out string? error)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(text);
        pattern = null;
        error = Build(name, level, meter, text, draft: false, ref pattern);
        return error is null;
    }

    /// <summary>
    /// A rhythm still being written (<see cref="RhythmBuilder"/>): as <see cref="TryParse"/>, but the last bar may be
    /// short (or empty, after a trailing <c>|</c>), there may be no notes yet, and the last note may be tied. Every bar
    /// before the last must still be exactly full.
    /// </summary>
    public static bool TryParseDraft(Meter meter, string text, out RhythmPattern? pattern, out string? error)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(text);
        pattern = null;
        error = Build("Draft", 0, meter, text, draft: true, ref pattern);
        return error is null;
    }

    private static readonly char[] Spaces = [' ', '\t', '\r', '\n'];

    private static string? Build(string name, int level, Meter meter, string text, bool draft, ref RhythmPattern? pattern)
    {
        var notes = new List<RhythmNote>();
        var barEnds = new List<int>();      // note count at each "|"
        var tokens = text.Replace("[", " [ ", StringComparison.Ordinal).Replace("]", " ] ", StringComparison.Ordinal)
            .Replace("|", " | ", StringComparison.Ordinal)
            .Split(Spaces, StringSplitOptions.RemoveEmptyEntries);
        Tuplet? tuplet = null;
        var groups = 0;
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token == "|")
            {
                if (tuplet is not null) return "a tuplet can't cross a bar line.";
                barEnds.Add(notes.Count);
                continue;
            }
            if (token == "[")
            {
                if (tuplet is not null || i + 1 >= tokens.Length || !int.TryParse(tokens[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var actual))
                    return "a tuplet starts with its count, like [3 e e e].";
                int? normal = actual switch { 3 => 2, 5 or 6 or 7 => 4, _ => null };
                if (normal is null) return $"{actual}-tuplets aren't supported (3, 5, 6 or 7).";
                tuplet = new Tuplet(actual, normal.Value, groups++);
                i++;
                continue;
            }
            if (token == "]")
            {
                if (tuplet is null) return "']' without '['.";
                tuplet = null;
                continue;
            }
            if (ParseNote(token, tuplet, out var note) is { } bad) return bad;
            notes.Add(note!);
        }
        if (tuplet is not null) return "unclosed tuplet.";
        barEnds.Add(notes.Count);
        if (barEnds.Count > MaxBars) return $"at most {MaxBars} bars.";
        if (notes.Count == 0 && !draft) return "no notes.";

        var at = 0;
        var from = 0;
        for (var bar = 0; bar < barEnds.Count; bar++)
        {
            var barStart = at;
            for (var i = from; i < barEnds[bar]; i++)
            {
                var ticks = notes[i].ComputeTicks();
                notes[i] = notes[i] with { Start = at, Ticks = ticks };
                at += ticks;
            }
            from = barEnds[bar];
            var lastOfDraft = draft && bar == barEnds.Count - 1;
            if (lastOfDraft ? at - barStart > meter.BarTicks : at - barStart != meter.BarTicks)
                return $"{(barEnds.Count > 1 ? $"bar {bar + 1}'s" : "the")} notes add up to {at - barStart} ticks; a {meter.Signature} bar is {meter.BarTicks} " +
                       $"({meter.BeatsText(at - barStart)} of {meter.BeatsText(meter.BarTicks)}).";
        }
        if (!draft && notes[^1].TiedToNext) return "the last note can't be tied.";
        pattern = new RhythmPattern(name, level, meter, notes, barEnds.Count, string.Join(' ', text.Split(Spaces, StringSplitOptions.RemoveEmptyEntries)));
        return null;
    }

    /// <summary>One symbol ("e.", "rq", "s~"); the error, or null with <paramref name="note"/> set.</summary>
    private static string? ParseNote(string token, Tuplet? tuplet, out RhythmNote? note)
    {
        note = null;
        var s = token;
        var rest = s.StartsWith('r');
        if (rest) s = s[1..];
        var tie = s.EndsWith('~');
        if (tie) s = s[..^1];
        var dots = 0;
        while (s.EndsWith('.'))
        {
            dots++;
            s = s[..^1];
        }
        NoteLength? length = s switch
        {
            "w" => NoteLength.Whole,
            "h" => NoteLength.Half,
            "q" => NoteLength.Quarter,
            "e" => NoteLength.Eighth,
            "s" => NoteLength.Sixteenth,
            "t" => NoteLength.ThirtySecond,
            _ => null,
        };
        if (length is null) return $"'{token}' isn't a note (w h q e s t, r for a rest, . for a dot, ~ for a tie, | between bars).";
        if (rest && tie) return $"a rest can't be tied ('{token}').";
        note = new RhythmNote(length.Value, dots, rest, tie, tuplet);
        return null;
    }
}
