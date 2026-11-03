namespace GuitarPractice.Domain.Rhythm;

/// <summary>One expected note: how far off it was played (ms, + = late), or null when it was missed. A note played
/// outside the window but still nearer to it than to its neighbours is missed (no points) with
/// <see cref="WayOffMs"/> saying how far off it came, rather than a miss plus an extra note.</summary>
public sealed record NoteTiming(int Index, double ExpectedSeconds, double? OffsetMs, double? WayOffMs = null)
{
    public bool IsHit => OffsetMs is not null;

    public bool IsWayOff => WayOffMs is not null;
}

/// <summary>A take's result. <see cref="Percent"/> = timing points (full within <see cref="RhythmScore.TightMs"/>,
/// falling to 0 at the window's edge) over (notes + extra notes).</summary>
public sealed record RhythmResult(IReadOnlyList<NoteTiming> Notes, int Extra, int Percent, double WindowMs)
{
    public int Hits => Notes.Count(n => n.IsHit);
    public int Missed => Notes.Count - Hits;

    /// <summary>Missed notes that were played, but too far off to score (see <see cref="NoteTiming.WayOffMs"/>).</summary>
    public int WayOff => Notes.Count(n => n.IsWayOff);

    /// <summary>Average signed offset of the hits (+ = you tend to play late); null without hits.</summary>
    public double? MeanOffsetMs => Hits == 0 ? null : Notes.Where(n => n.IsHit).Average(n => n.OffsetMs!.Value);

    /// <summary>Average distance from the beat of the hits; null without hits.</summary>
    public double? MeanAbsOffsetMs => Hits == 0 ? null : Notes.Where(n => n.IsHit).Average(n => Math.Abs(n.OffsetMs!.Value));
}

/// <summary>
/// Matches what was played to the notes that should have been played. Each played onset goes to the nearest expected
/// note; it counts as a hit if it's within the window (and that note doesn't already have a closer one), otherwise as
/// an extra note. The window is <see cref="MaxWindowMs"/>, narrowed for fast rhythms to 45 % of the shortest gap
/// between notes so a note can't be credited to its neighbour. Afterwards an extra note within twice the window of a
/// missed note on either side of it (the nearer one if both were missed) is taken as that note played way off: it
/// stays missed, with no points, but isn't also counted extra (user's take 2026-11-02: a 3& played 160 ms early read
/// as a miss and an extra note). Further off than that, it's more likely a different note than a late one.
/// </summary>
public static class RhythmScore
{
    public const double TightMs = 20;
    public const double MaxWindowMs = 100;

    public static double Window(IReadOnlyList<double> expectedSeconds)
    {
        ArgumentNullException.ThrowIfNull(expectedSeconds);
        var window = MaxWindowMs;
        for (var i = 1; i < expectedSeconds.Count; i++)
            window = Math.Min(window, 0.45 * (expectedSeconds[i] - expectedSeconds[i - 1]) * 1000);
        return Math.Max(window, TightMs + 5);
    }

    /// <param name="expectedSeconds">When each note should sound, ascending.</param>
    /// <param name="playedSeconds">Detected onsets of what was played, same clock, ascending.</param>
    public static RhythmResult Score(IReadOnlyList<double> expectedSeconds, IReadOnlyList<double> playedSeconds)
    {
        ArgumentNullException.ThrowIfNull(expectedSeconds);
        ArgumentNullException.ThrowIfNull(playedSeconds);
        var window = Window(expectedSeconds);
        var best = new double?[expectedSeconds.Count];
        var outside = new List<double>(); // extras outside every window
        var extra = 0;
        foreach (var played in playedSeconds)
        {
            var nearest = Nearest(expectedSeconds, played);
            if (nearest < 0)
            {
                extra++;
                continue;
            }
            var offset = (played - expectedSeconds[nearest]) * 1000;
            if (Math.Abs(offset) > window)
            {
                extra++;
                outside.Add(played);
                continue;
            }
            if (best[nearest] is { } already)
            {
                extra++; // two onsets for one note: the farther one is extra
                if (Math.Abs(offset) >= Math.Abs(already)) continue;
            }
            best[nearest] = offset;
        }

        // A missed note with an extra onset beside it (within twice the window; the nearest such onset) was played way off.
        var wayOff = new double?[expectedSeconds.Count];
        foreach (var played in outside)
        {
            var note = -1;
            var offset = double.MaxValue;
            for (var i = 0; i < expectedSeconds.Count; i++)
            {
                var o = (played - expectedSeconds[i]) * 1000;
                if (best[i] is null && Math.Abs(o) <= 2 * window && Math.Abs(o) < Math.Abs(offset) && Between(expectedSeconds, i, played))
                {
                    note = i;
                    offset = o;
                }
            }
            if (note < 0 || (wayOff[note] is { } other && Math.Abs(other) <= Math.Abs(offset))) continue;
            if (wayOff[note] is null) extra--;
            wayOff[note] = offset;
        }

        var notes = expectedSeconds.Select((e, i) => new NoteTiming(i, e, best[i], wayOff[i])).ToList();
        var points = notes.Sum(n => n.OffsetMs is { } o ? Points(Math.Abs(o), window) : 0);
        var total = notes.Count + extra;
        var percent = total == 0 ? 0 : (int)Math.Round(100 * points / total);
        return new RhythmResult(notes, extra, percent, window);
    }

    /// <summary>1 within <see cref="TightMs"/>, then linearly down to 0 at the window's edge.</summary>
    public static double Points(double absOffsetMs, double windowMs) =>
        absOffsetMs <= TightMs ? 1 : Math.Max(0, 1 - (absOffsetMs - TightMs) / (windowMs - TightMs));

    /// <summary>No other note lies between note <paramref name="i"/> and the onset (it's one of the two notes either side).</summary>
    private static bool Between(IReadOnlyList<double> sorted, int i, double played) =>
        played < sorted[i] ? i == 0 || sorted[i - 1] <= played : i + 1 == sorted.Count || sorted[i + 1] >= played;

    private static int Nearest(IReadOnlyList<double> sorted, double value)
    {
        var best = -1;
        var distance = double.MaxValue;
        for (var i = 0; i < sorted.Count; i++)
        {
            var d = Math.Abs(sorted[i] - value);
            if (d < distance)
            {
                distance = d;
                best = i;
            }
        }
        return best;
    }
}
