namespace GuitarPractice.Domain.Rhythm;

/// <summary>
/// The built-in rhythms, one bar each, by meter and level (1 basic … 5 hardest). Syntax: <see cref="RhythmPattern.Parse"/>.
/// </summary>
public static class RhythmLibrary
{
    public static IReadOnlyList<RhythmPattern> All { get; } = Build();

    public static IReadOnlyList<RhythmPattern> For(Meter meter) => [.. All.Where(p => p.Meter.Equals(meter))];

    private static List<RhythmPattern> Build()
    {
        var list = new List<RhythmPattern>();
        void Add(Meter meter, int level, string name, string text) => list.Add(RhythmPattern.Parse(name, level, meter, text));

        var m = Meter.FourFour;
        Add(m, 1, "Quarter notes", "q q q q");
        Add(m, 1, "Half and quarters", "h q q");
        Add(m, 1, "Eighth notes", "e e e e e e e e");
        Add(m, 1, "Quarters and rests", "q rq q rq");
        Add(m, 2, "Eighths and quarters", "q e e q e e");
        Add(m, 2, "Offbeat eighths", "re e re e re e re e");
        Add(m, 2, "Dotted quarter and eighth", "q. e q. e");
        Add(m, 2, "Sixteenths on 1 and 3", "s s s s q s s s s q");
        Add(m, 3, "Gallop", "e s s e s s e s s e s s");
        Add(m, 3, "Reverse gallop", "s s e s s e s s e s s e");
        Add(m, 3, "Dotted eighth and sixteenth", "e. s e. s e. s e. s");
        Add(m, 3, "Syncopated eighths", "e q e q e e");
        Add(m, 3, "Tied across the beat", "q e e~ e e q");
        Add(m, 4, "Eighth-note triplets", "[3 e e e] [3 e e e] q q");
        Add(m, 4, "Triplets with rests", "[3 e re e] [3 re e e] [3 e e re] q");
        Add(m, 4, "Sixteenth syncopation", "s e s s e s s e s s e s");
        Add(m, 4, "Funk sixteenths", "s rs s s rs s s s s s rs s q");
        Add(m, 5, "Quintuplets", "[5 s s s s s] [5 s s s s s] q q");
        Add(m, 5, "Sextuplets", "[6 s s s s s s] q [6 s s s s s s] q");
        Add(m, 5, "Mixed groupings", "q [3 e e e] e. s [5 s s s s s]");
        Add(m, 5, "Thirty-second bursts", "t t t t e q t t t t e q");

        m = Meter.ThreeFour;
        Add(m, 1, "Quarter notes", "q q q");
        Add(m, 1, "Half and quarter", "h q");
        Add(m, 1, "Dotted half", "h.");
        Add(m, 2, "Eighth notes", "e e e e e e");
        Add(m, 2, "Eighths and quarters", "q e e q");
        Add(m, 3, "Dotted quarter and eighth", "q. e q");
        Add(m, 3, "Offbeat eighths", "re e re e re e");
        Add(m, 4, "Gallop", "e s s e s s e s s");
        Add(m, 4, "Triplets", "[3 e e e] q [3 e e e]");

        m = Meter.TwoFour;
        Add(m, 1, "Quarter notes", "q q");
        Add(m, 2, "Eighth notes", "e e e e");
        Add(m, 3, "Sixteenths and a quarter", "s s s s q");
        Add(m, 3, "Gallop", "e s s e s s");
        Add(m, 4, "Triplet and two eighths", "[3 e e e] e e");

        m = Meter.SixEight;
        Add(m, 1, "Eighth notes", "e e e e e e");
        Add(m, 1, "Dotted quarters", "q. q.");
        Add(m, 2, "Quarter and eighth", "q e q e");
        Add(m, 2, "Eighth and quarter", "e q e q");
        Add(m, 3, "Sixteenths", "e s s e e s s e");
        Add(m, 3, "Rest and two eighths", "q. re e e");
        Add(m, 4, "Dotted eighths", "e. s e e. s e");

        m = Meter.NineEight;
        Add(m, 1, "Dotted quarters", "q. q. q.");
        Add(m, 2, "Quarter and eighth", "q e q e q e");
        Add(m, 3, "Mixed", "e e e q e q.");

        m = Meter.TwelveEight;
        Add(m, 1, "Dotted quarters", "q. q. q. q.");
        Add(m, 2, "Shuffle", "q e q e q e q e");
        Add(m, 3, "Mixed", "q. e e e q e q.");
        Add(m, 4, "Sixteenths", "e s s e q e e e e q.");

        m = Meter.FiveFour;
        Add(m, 1, "Quarter notes", "q q q q q");
        Add(m, 2, "Eighths and quarters", "q e e q e e q");
        Add(m, 3, "Dotted quarter", "q. e q q q");
        Add(m, 3, "Three plus two", "e e e e e e q e e");
        Add(m, 4, "Syncopated", "e q e q e e q");

        m = Meter.SevenFour;
        Add(m, 1, "Quarter notes", "q q q q q q q");
        Add(m, 2, "Halves and quarters", "h q h q q");
        Add(m, 3, "Eighth groups", "e e q e e q e e q q");
        Add(m, 4, "Syncopated four plus three", "e q e q e e q e e q");

        m = Meter.FiveEight;
        Add(m, 1, "Eighth notes", "e e e e e");
        Add(m, 2, "Dotted quarter and quarter", "q. q");
        Add(m, 3, "Quarter, eighth, quarter", "q e q");
        Add(m, 3, "Sixteenths", "e s s e e e");

        m = Meter.SevenEight;
        Add(m, 1, "Eighth notes", "e e e e e e e");
        Add(m, 2, "Quarters and dotted quarter", "q q q.");
        Add(m, 3, "Mixed", "e e q e e e");
        Add(m, 4, "Sixteenths", "s s e s s e e e e");
        Add(m, 4, "Syncopated", "e q e q e");

        return list;
    }
}
