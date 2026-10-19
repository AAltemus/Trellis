namespace GuitarPractice.Domain.EarTraining;

public enum KeyMode
{
    Major = 0,

    /// <summary>Natural minor (Aeolian).</summary>
    Minor = 1,
}

/// <summary>A key: tonic pitch class (C = 0 … B = 11) and mode.</summary>
public sealed record MusicKey(int Tonic, KeyMode Mode)
{
    private static readonly int[] MajorSteps = [0, 2, 4, 5, 7, 9, 11];
    private static readonly int[] MinorSteps = [0, 2, 3, 5, 7, 8, 10];
    private static readonly string[] Sharps = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    private static readonly string[] Flats = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"];

    /// <summary>All 24 keys, majors then minors, from C.</summary>
    public static IReadOnlyList<MusicKey> All { get; } =
        [.. Enumerable.Range(0, 12).Select(t => new MusicKey(t, KeyMode.Major)), .. Enumerable.Range(0, 12).Select(t => new MusicKey(t, KeyMode.Minor))];

    public int PitchClass => ((Tonic % 12) + 12) % 12;

    public IReadOnlyList<int> ScaleSteps => Mode == KeyMode.Major ? MajorSteps : MinorSteps;

    /// <summary>Semitones above the tonic of scale degree <paramref name="degree"/> (0 = tonic; 7 = the octave, and so on).</summary>
    public int DegreeOffset(int degree)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(degree);
        return ScaleSteps[degree % 7] + 12 * (degree / 7);
    }

    /// <summary>Flat spellings for F, Bb, Eb, Ab, Db major and their relative minors (F# major / D# minor use sharps).</summary>
    public bool PrefersFlats
    {
        get
        {
            var major = Mode == KeyMode.Major ? PitchClass : (PitchClass + 3) % 12;
            return major is 5 or 10 or 3 or 8 or 1;
        }
    }

    public string NoteName(int pitchClass) => (PrefersFlats ? Flats : Sharps)[((pitchClass % 12) + 12) % 12];

    public string Name => $"{NoteName(PitchClass)} {(Mode == KeyMode.Major ? "major" : "minor")}";

    /// <summary>The tonic in a guitar-friendly octave: E3 (MIDI 52) … D#4 (63).</summary>
    public int TonicMidi => 52 + ((PitchClass - 4 + 12) % 12);

    public override string ToString() => Name;
}

public enum ChordQuality
{
    Major,
    Minor,
    Diminished,
    Augmented,
    MajorSeventh,
    DominantSeventh,
    MinorSeventh,
    HalfDiminishedSeventh,
    DiminishedSeventh,
    MinorMajorSeventh,
    AugmentedMajorSeventh,
    Sus2,
    Sus4,
    MajorAdd9,
    MinorAdd9,
    MajorSixth,
    MinorSixth,
    MajorNinth,
    DominantNinth,
    MinorNinth,
}

/// <summary>
/// Which notes of the key are stacked on a scale degree. Every type uses only the key's notes, so some don't exist on
/// some degrees (sus4 on IV in major would be F–B–C): <see cref="MusicTheory.Exists"/> says which do.
/// </summary>
public enum ChordType
{
    /// <summary>1–3–5.</summary>
    Triad = 0,

    /// <summary>1–3–5–7.</summary>
    Seventh = 1,

    /// <summary>1–2–5.</summary>
    Sus2 = 2,

    /// <summary>1–4–5.</summary>
    Sus4 = 3,

    /// <summary>1–3–5–9.</summary>
    Add9 = 4,

    /// <summary>1–3–5–6.</summary>
    Sixth = 5,

    /// <summary>1–3–5–7–9.</summary>
    Ninth = 6,
}

public static class MusicTheory
{
    private static readonly string[] IntervalShort = ["P1", "m2", "M2", "m3", "M3", "P4", "TT", "P5", "m6", "M6", "m7", "M7", "P8"];
    private static readonly string[] IntervalLong =
    [
        "unison", "minor 2nd", "major 2nd", "minor 3rd", "major 3rd", "perfect 4th", "tritone", "perfect 5th",
        "minor 6th", "major 6th", "minor 7th", "major 7th", "octave",
    ];
    private static readonly string[] Numerals = ["I", "II", "III", "IV", "V", "VI", "VII"];

    /// <summary>Scale steps above the root for each <see cref="ChordType"/> (7 = the octave, so 8 = the 9th).</summary>
    private static int[] Steps(ChordType type) => type switch
    {
        ChordType.Triad => [0, 2, 4],
        ChordType.Seventh => [0, 2, 4, 6],
        ChordType.Sus2 => [0, 1, 4],
        ChordType.Sus4 => [0, 3, 4],
        ChordType.Add9 => [0, 2, 4, 8],
        ChordType.Sixth => [0, 2, 4, 5],
        ChordType.Ninth => [0, 2, 4, 6, 8],
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>"P5" for 7 semitones (1–12).</summary>
    public static string IntervalShortName(int semitones) => IntervalShort[CheckInterval(semitones)];

    /// <summary>"perfect 5th" for 7 semitones (1–12).</summary>
    public static string IntervalLongName(int semitones) => IntervalLong[CheckInterval(semitones)];

    private static int CheckInterval(int semitones)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(semitones, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(semitones, 12);
        return semitones;
    }

    /// <summary>Semitones above the chord root of each chord tone of the diatonic chord on <paramref name="degree"/> (thirds stacked in the key).</summary>
    public static IReadOnlyList<int> ChordTones(MusicKey key, int degree, bool seventh) =>
        ChordTones(key, degree, seventh ? ChordType.Seventh : ChordType.Triad);

    /// <summary>Semitones above the root of each tone of the <paramref name="type"/> chord built from the key's notes on <paramref name="degree"/>.</summary>
    public static IReadOnlyList<int> ChordTones(MusicKey key, int degree, ChordType type)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegative(degree);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(degree, 6);
        var root = key.DegreeOffset(degree);
        return [.. Steps(type).Select(step => key.DegreeOffset(degree + step) - root)];
    }

    public static ChordQuality Quality(MusicKey key, int degree, bool seventh) =>
        Quality(key, degree, seventh ? ChordType.Seventh : ChordType.Triad);

    public static ChordQuality Quality(MusicKey key, int degree, ChordType type) =>
        QualityOf(ChordTones(key, degree, type))
        ?? throw new InvalidOperationException($"No {type} chord on degree {degree + 1} of {key?.Name}.");

    /// <summary>
    /// False where the key's notes don't make a real chord of that type (e.g. sus4 on IV in major: F–B–C; a 9th on iii:
    /// E–G–B–D–F has a ♭9). Triads and sevenths exist on every degree.
    /// </summary>
    public static bool Exists(MusicKey key, int degree, ChordType type) => QualityOf(ChordTones(key, degree, type)) is not null;

    /// <summary>The named chord these tones (semitones above the root) make, or null.</summary>
    private static ChordQuality? QualityOf(IReadOnlyList<int> t) => string.Join(",", t) switch
    {
        "0,4,7" => ChordQuality.Major,
        "0,3,7" => ChordQuality.Minor,
        "0,3,6" => ChordQuality.Diminished,
        "0,4,8" => ChordQuality.Augmented,
        "0,4,7,11" => ChordQuality.MajorSeventh,
        "0,4,7,10" => ChordQuality.DominantSeventh,
        "0,3,7,10" => ChordQuality.MinorSeventh,
        "0,3,6,10" => ChordQuality.HalfDiminishedSeventh,
        "0,3,6,9" => ChordQuality.DiminishedSeventh,
        "0,3,7,11" => ChordQuality.MinorMajorSeventh,
        "0,4,8,11" => ChordQuality.AugmentedMajorSeventh,
        "0,2,7" => ChordQuality.Sus2,
        "0,5,7" => ChordQuality.Sus4,
        "0,4,7,14" => ChordQuality.MajorAdd9,
        "0,3,7,14" => ChordQuality.MinorAdd9,
        "0,4,7,9" => ChordQuality.MajorSixth,
        "0,3,7,9" => ChordQuality.MinorSixth,
        "0,4,7,11,14" => ChordQuality.MajorNinth,
        "0,4,7,10,14" => ChordQuality.DominantNinth,
        "0,3,7,10,14" => ChordQuality.MinorNinth,
        _ => null,
    };

    /// <summary>"minor", "dominant 7th", "half-diminished 7th", "sus4", "minor 9th" …</summary>
    public static string QualityName(ChordQuality quality) => quality switch
    {
        ChordQuality.Major => "major",
        ChordQuality.Minor => "minor",
        ChordQuality.Diminished => "diminished",
        ChordQuality.Augmented => "augmented",
        ChordQuality.MajorSeventh => "major 7th",
        ChordQuality.DominantSeventh => "dominant 7th",
        ChordQuality.MinorSeventh => "minor 7th",
        ChordQuality.HalfDiminishedSeventh => "half-diminished 7th",
        ChordQuality.DiminishedSeventh => "diminished 7th",
        ChordQuality.MinorMajorSeventh => "minor-major 7th",
        ChordQuality.AugmentedMajorSeventh => "augmented major 7th",
        ChordQuality.Sus2 => "sus2",
        ChordQuality.Sus4 => "sus4",
        ChordQuality.MajorAdd9 => "add9",
        ChordQuality.MinorAdd9 => "minor add9",
        ChordQuality.MajorSixth => "6th",
        ChordQuality.MinorSixth => "minor 6th",
        ChordQuality.MajorNinth => "major 9th",
        ChordQuality.DominantNinth => "dominant 9th",
        _ => "minor 9th",
    };

    /// <summary>Roman numeral in the key: I, ii, vii°, V7, Imaj7, iiø7 …</summary>
    public static string Numeral(MusicKey key, int degree, bool seventh) =>
        Numeral(key, degree, seventh ? ChordType.Seventh : ChordType.Triad);

    /// <summary>
    /// Roman numeral in the key: I, ii, vii°, V7, Imaj7, iiø7, Vsus4, Iadd9, ii6, V9, vi9 … Upper case = major third;
    /// sus chords (no third) take the case of the degree's triad.
    /// </summary>
    public static string Numeral(MusicKey key, int degree, ChordType type)
    {
        var quality = Quality(key, degree, type);
        var minorThird = ChordTones(key, degree, ChordType.Triad)[1] == 3;
        var numeral = minorThird ? Numerals[degree].ToLowerInvariant() : Numerals[degree];
        return numeral + quality switch
        {
            ChordQuality.Diminished => "°",
            ChordQuality.Augmented => "+",
            ChordQuality.MajorSeventh => "maj7",
            ChordQuality.DominantSeventh or ChordQuality.MinorSeventh => "7",
            ChordQuality.HalfDiminishedSeventh => "ø7",
            ChordQuality.DiminishedSeventh => "°7",
            ChordQuality.MinorMajorSeventh => "(maj7)",
            ChordQuality.AugmentedMajorSeventh => "+maj7",
            ChordQuality.Sus2 => "sus2",
            ChordQuality.Sus4 => "sus4",
            ChordQuality.MajorAdd9 or ChordQuality.MinorAdd9 => "add9",
            ChordQuality.MajorSixth or ChordQuality.MinorSixth => "6",
            ChordQuality.MajorNinth => "maj9",
            ChordQuality.DominantNinth or ChordQuality.MinorNinth => "9",
            _ => "",
        };
    }

    /// <summary>Chord symbol in the key's spelling: G7, Dm, Bdim, Cmaj7, Bm7b5 …</summary>
    public static string ChordName(MusicKey key, int degree, bool seventh) =>
        ChordName(key, degree, seventh ? ChordType.Seventh : ChordType.Triad);

    /// <summary>Chord symbol in the key's spelling: G7, Dm, Bdim, Cmaj7, Bm7b5, Gsus4, Dm(add9), Dm6, Cmaj9, Am9 …</summary>
    public static string ChordName(MusicKey key, int degree, ChordType type)
    {
        ArgumentNullException.ThrowIfNull(key);
        var root = key.NoteName(key.PitchClass + key.DegreeOffset(degree));
        return root + Quality(key, degree, type) switch
        {
            ChordQuality.Major => "",
            ChordQuality.Minor => "m",
            ChordQuality.Diminished => "dim",
            ChordQuality.Augmented => "aug",
            ChordQuality.MajorSeventh => "maj7",
            ChordQuality.DominantSeventh => "7",
            ChordQuality.MinorSeventh => "m7",
            ChordQuality.HalfDiminishedSeventh => "m7b5",
            ChordQuality.DiminishedSeventh => "dim7",
            ChordQuality.MinorMajorSeventh => "m(maj7)",
            ChordQuality.AugmentedMajorSeventh => "aug(maj7)",
            ChordQuality.Sus2 => "sus2",
            ChordQuality.Sus4 => "sus4",
            ChordQuality.MajorAdd9 => "add9",
            ChordQuality.MinorAdd9 => "m(add9)",
            ChordQuality.MajorSixth => "6",
            ChordQuality.MinorSixth => "m6",
            ChordQuality.MajorNinth => "maj9",
            ChordQuality.DominantNinth => "9",
            _ => "m9",
        };
    }
}
