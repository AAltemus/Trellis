namespace GuitarPractice.Domain.Rhythm;

/// <summary>
/// A time signature and how its bar is clicked: <see cref="Groups"/> are the beats, in units of the denominator
/// (4/4 = 1+1+1+1 quarters; 6/8 = 3+3 eighths; 7/8 = 2+2+3 eighths). The click sounds on each group's start, the
/// first one accented. Positions are in ticks, <see cref="TicksPerQuarter"/> to a quarter note (divisible by 2, 3, 4,
/// 5, 6, 8, 10, 12 and 16, so tuplets and 32nds land on whole ticks).
/// </summary>
public sealed record Meter(int Numerator, int Denominator, IReadOnlyList<int> Groups)
{
    public const int TicksPerQuarter = 480;

    public static Meter TwoFour { get; } = Simple(2);
    public static Meter ThreeFour { get; } = Simple(3);
    public static Meter FourFour { get; } = Simple(4);
    public static Meter FiveFour { get; } = Simple(5);
    public static Meter SevenFour { get; } = Simple(7);
    public static Meter FiveEight { get; } = new(5, 8, [3, 2]);
    public static Meter SixEight { get; } = new(6, 8, [3, 3]);
    public static Meter SevenEight { get; } = new(7, 8, [2, 2, 3]);
    public static Meter NineEight { get; } = new(9, 8, [3, 3, 3]);
    public static Meter TwelveEight { get; } = new(12, 8, [3, 3, 3, 3]);

    /// <summary>The meters the trainer offers, common ones first.</summary>
    public static IReadOnlyList<Meter> All { get; } =
        [FourFour, ThreeFour, TwoFour, SixEight, NineEight, TwelveEight, FiveFour, SevenFour, FiveEight, SevenEight];

    /// <summary>Odd meters (5 or 7 beats/units): the trainer logs these to the "Odd meters" block by default.</summary>
    public bool IsOdd => Numerator is 5 or 7;

    /// <summary>"4/4", "7/8".</summary>
    public string Signature => $"{Numerator}/{Denominator}";

    /// <summary>"4/4", "6/8 (3+3)", "7/8 (2+2+3)": the grouping is shown where it isn't one beat per unit.</summary>
    public string Label => Groups.All(g => g == 1) ? Signature : $"{Signature} ({string.Join('+', Groups)})";

    /// <summary>Ticks in one unit (the denominator's note).</summary>
    public int UnitTicks => TicksPerQuarter * 4 / Denominator;

    public int BarTicks => Numerator * UnitTicks;

    /// <summary>Where each click (group start) falls, in ticks from the bar start.</summary>
    public IReadOnlyList<int> BeatTicks
    {
        get
        {
            var ticks = new List<int>(Groups.Count);
            var at = 0;
            foreach (var g in Groups)
            {
                ticks.Add(at);
                at += g * UnitTicks;
            }
            return ticks;
        }
    }

    /// <summary>The click (group) that <paramref name="tick"/> falls in, 0-based.</summary>
    public int BeatAt(int tick)
    {
        var beats = BeatTicks;
        var i = 0;
        while (i + 1 < beats.Count && beats[i + 1] <= tick) i++;
        return i;
    }

    /// <summary>The meter with this signature ("7/8"), or null.</summary>
    public static Meter? Find(string? signature) => All.FirstOrDefault(m => m.Signature == signature);

    private static Meter Simple(int beats) => new(beats, 4, [.. Enumerable.Repeat(1, beats)]);

    public bool Equals(Meter? other) =>
        other is not null && Numerator == other.Numerator && Denominator == other.Denominator && Groups.SequenceEqual(other.Groups);

    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator, Groups.Count);
}
