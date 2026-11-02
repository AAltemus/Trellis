namespace GuitarPractice.Domain.Rhythm;

/// <summary>
/// Random rhythms at a level (1 basic … 5 hardest), built beat by beat from small "cells" (a beat's worth of notes in
/// the text syntax), so every result fills the bar exactly and reads like the library's rhythms. Each level adds cells
/// to the ones below it (level 2: 8ths; 3: 16ths, dotted 8ths, syncopation across two beats; 4: triplets, 16th rests,
/// ties; 5: quintuplets, sextuplets, 32nds), and every rhythm uses at least one cell of its own level. Pure and
/// deterministic for a given <see cref="Random"/> seed.
/// </summary>
public static class RhythmGenerator
{
    public const int MinLevel = 1;
    public const int MaxLevel = 5;

    private sealed record Cell(int Level, string Text);

    private static readonly int Quarter = Meter.TicksPerQuarter;
    private static readonly int DottedQuarter = Meter.TicksPerQuarter * 3 / 2;

    /// <summary>Cells one beat long, by the beat's length in ticks (a quarter; a dotted quarter in 6/8, 9/8, 12/8 and
    /// the 3-groups of 5/8 and 7/8).</summary>
    private static readonly Dictionary<int, Cell[]> BeatCells = new()
    {
        [Quarter] =
        [
            new(1, "q"), new(1, "q"), new(1, "rq"),
            new(2, "e e"), new(2, "re e"),
            new(3, "s s s s"), new(3, "e s s"), new(3, "s s e"), new(3, "e. s"),
            new(4, "[3 e e e]"), new(4, "s e s"), new(4, "rs s rs s"), new(4, "s e."), new(4, "[3 e re e]"), new(4, "rs s s s"),
            new(5, "[5 s s s s s]"), new(5, "[6 s s s s s s]"), new(5, "t t t t e"), new(5, "e t t t t"), new(5, "[3 s s s] e"),
        ],
        [DottedQuarter] =
        [
            new(1, "q."), new(1, "q e"), new(1, "e e e"),
            new(2, "e q"), new(2, "re e e"), new(2, "q re"),
            new(3, "e s s e"), new(3, "s s e e"), new(3, "e e s s"), new(3, "e. s e"),
            new(4, "s s s s s s"), new(4, "s e s e"), new(4, "re s s e"), new(4, "e s s s s"), new(4, "s s s s e"),
            new(5, "t t t t e e"), new(5, "e t t t t e"), new(5, "[5 s s s s s] e"),
        ],
    };

    /// <summary>Cells two quarter beats long (used where two quarter beats follow each other).</summary>
    private static readonly Cell[] TwoBeatCells =
    [
        new(1, "h"),
        new(2, "q. e"),
        new(3, "e q e"), new(3, "e q."),
        new(4, "e e~ e e"), new(4, "e. s~ e e"),
    ];

    /// <summary>
    /// A random rhythm of <paramref name="bars"/> bars in <paramref name="meter"/> at <paramref name="level"/>, named
    /// "Random · level 3". Always at least two notes to play.
    /// </summary>
    public static RhythmPattern Generate(Meter meter, int level, Random random, int bars = 1)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(random);
        level = Math.Clamp(level, MinLevel, MaxLevel);
        bars = Math.Clamp(bars, 1, RhythmPattern.MaxBars);
        var name = $"Random · level {level}";
        for (var attempt = 0; ; attempt++)
        {
            var pattern = RhythmPattern.Parse(name, level, meter, string.Join(" | ", Enumerable.Range(0, bars).Select(_ => Bar(meter, level, random))));
            if (pattern.Onsets.Count >= 2 || attempt >= 20) return pattern;
        }
    }

    private static string Bar(Meter meter, int level, Random random)
    {
        var beats = meter.Groups.Select(g => g * meter.UnitTicks).ToList();
        var cells = new List<Cell>();
        var spans = new List<int>();            // how many beats each cell covers
        for (var b = 0; b < beats.Count; b++)
        {
            var twoBeats = beats[b] == Quarter && b + 1 < beats.Count && beats[b + 1] == Quarter && random.NextDouble() < 0.3;
            var pool = twoBeats ? TwoBeatCells : BeatCells[beats[b]];
            cells.Add(Pick(pool, level, random));
            spans.Add(twoBeats ? 2 : 1);
            if (twoBeats) b++;
        }

        // Make sure the rhythm sounds like its level: swap in a one-beat cell of exactly that level if none came up
        // (splitting a two-beat cell into two beats when that's all there is).
        if (cells.All(c => c.Level < level))
        {
            var slots = Enumerable.Range(0, cells.Count).Where(i => spans[i] == 1).ToList();
            var i = slots.Count > 0 ? slots[random.Next(slots.Count)] : random.Next(cells.Count);
            if (spans[i] == 2)
            {
                cells[i] = Pick(BeatCells[Quarter], level, random);
                spans[i] = 1;
                cells.Insert(i + 1, Pick(BeatCells[Quarter], level, random));
                spans.Insert(i + 1, 1);
            }
            var exact = BeatCells[beats[BeatIndex(spans, i)]].Where(c => c.Level == level).ToArray();
            if (exact.Length > 0) cells[i] = exact[random.Next(exact.Length)];
        }

        // From level 4, sometimes tie a beat's last note into the next beat's first.
        var texts = cells.Select(c => c.Text).ToList();
        if (level >= 4)
            for (var i = 0; i + 1 < texts.Count; i++)
                if (random.NextDouble() < 0.2 && CanTie(texts[i], texts[i + 1])) texts[i] += "~";
        return string.Join(' ', texts);
    }

    /// <summary>The bar's beat that cell <paramref name="cell"/> starts on.</summary>
    private static int BeatIndex(List<int> spans, int cell) => spans.Take(cell).Sum();

    /// <summary>Weighted toward the level asked for: that level ×3, one below ×2, lower ×1.</summary>
    private static Cell Pick(Cell[] pool, int level, Random random)
    {
        var usable = pool.Where(c => c.Level <= level).ToArray();
        var weights = usable.Select(c => c.Level == level ? 3.0 : c.Level == level - 1 ? 2.0 : 1.0).ToArray();
        var pick = random.NextDouble() * weights.Sum();
        for (var i = 0; i < usable.Length; i++)
        {
            pick -= weights[i];
            if (pick < 0) return usable[i];
        }
        return usable[^1];
    }

    /// <summary>A plain note (not a rest, not in a tuplet, not already tied) at the end of one cell and the start of the next.</summary>
    private static bool CanTie(string from, string to)
    {
        var last = from.Split(' ')[^1];
        var first = to.Split(' ')[0];
        return !last.EndsWith(']') && !last.EndsWith('~') && !last.StartsWith('r') && !first.StartsWith('[') && !first.StartsWith('r');
    }
}
