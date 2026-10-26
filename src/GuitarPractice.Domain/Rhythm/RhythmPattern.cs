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
/// One bar of rhythm in a meter. The notes you play are the <see cref="Onsets"/>: every note that isn't a rest and isn't
/// the continuation of a tie. Built from a compact text (see <see cref="Parse"/>); the notes must fill the bar exactly.
/// </summary>
public sealed class RhythmPattern
{
    private RhythmPattern(string name, int level, Meter meter, IReadOnlyList<RhythmNote> notes)
    {
        Name = name;
        Level = level;
        Meter = meter;
        Notes = notes;
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

    /// <summary>1 (basic) … 5 (hardest).</summary>
    public int Level { get; }

    public Meter Meter { get; }

    public IReadOnlyList<RhythmNote> Notes { get; }

    /// <summary>Ticks (from the bar start) of the notes to play, in order.</summary>
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
    /// dot; an <c>r</c> in front for a rest (<c>rq</c>); a trailing <c>~</c> ties to the next note (<c>e~</c>); and
    /// <c>[3 e e e]</c> for a tuplet (3, 5, 6 or 7 notes in the time of 2, 4, 4 or 4). Example (4/4):
    /// <c>q e. s [3 e e e] q</c>. Throws <see cref="FormatException"/> on bad input or when the bar isn't filled exactly.
    /// </summary>
    public static RhythmPattern Parse(string name, int level, Meter meter, string text)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(text);
        var notes = new List<RhythmNote>();
        var tokens = text.Replace("[", " [ ", StringComparison.Ordinal).Replace("]", " ] ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Tuplet? tuplet = null;
        var groups = 0;
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token == "[")
            {
                if (tuplet is not null || i + 1 >= tokens.Length || !int.TryParse(tokens[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var actual))
                    throw new FormatException($"{name}: a tuplet starts with its count, like [3 e e e].");
                var normal = actual switch { 3 => 2, 5 or 6 or 7 => 4, _ => throw new FormatException($"{name}: {actual}-tuplets aren't supported.") };
                tuplet = new Tuplet(actual, normal, groups++);
                i++;
                continue;
            }
            if (token == "]")
            {
                if (tuplet is null) throw new FormatException($"{name}: ']' without '['.");
                tuplet = null;
                continue;
            }
            notes.Add(ParseNote(name, token, tuplet));
        }
        if (tuplet is not null) throw new FormatException($"{name}: unclosed tuplet.");

        var at = 0;
        for (var i = 0; i < notes.Count; i++)
        {
            var ticks = notes[i].ComputeTicks();
            notes[i] = notes[i] with { Start = at, Ticks = ticks };
            at += ticks;
        }
        if (at != meter.BarTicks)
            throw new FormatException($"{name}: the notes add up to {at} ticks; a {meter.Signature} bar is {meter.BarTicks}.");
        if (notes.Count > 0 && notes[^1].TiedToNext) throw new FormatException($"{name}: the last note can't be tied.");
        return new RhythmPattern(name, level, meter, notes);
    }

    private static RhythmNote ParseNote(string name, string token, Tuplet? tuplet)
    {
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
        var length = s switch
        {
            "w" => NoteLength.Whole,
            "h" => NoteLength.Half,
            "q" => NoteLength.Quarter,
            "e" => NoteLength.Eighth,
            "s" => NoteLength.Sixteenth,
            "t" => NoteLength.ThirtySecond,
            _ => throw new FormatException($"{name}: '{token}' isn't a note (w h q e s t, r for a rest, . for a dot, ~ for a tie)."),
        };
        if (rest && tie) throw new FormatException($"{name}: a rest can't be tied ('{token}').");
        return new RhythmNote(length, dots, rest, tie, tuplet);
    }
}
