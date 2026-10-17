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
    private static readonly int[] TriadSteps = [0, 2, 4];
    private static readonly int[] SeventhSteps = [0, 2, 4, 6];

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
    public static IReadOnlyList<int> ChordTones(MusicKey key, int degree, bool seventh)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegative(degree);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(degree, 6);
        var root = key.DegreeOffset(degree);
        return [.. (seventh ? SeventhSteps : TriadSteps).Select(step => key.DegreeOffset(degree + step) - root)];
    }

    public static ChordQuality Quality(MusicKey key, int degree, bool seventh)
    {
        var t = ChordTones(key, degree, seventh);
        return (t[1], t[2], seventh ? t[3] : -1) switch
        {
            (4, 7, -1) => ChordQuality.Major,
            (3, 7, -1) => ChordQuality.Minor,
            (3, 6, -1) => ChordQuality.Diminished,
            (4, 8, -1) => ChordQuality.Augmented,
            (4, 7, 11) => ChordQuality.MajorSeventh,
            (4, 7, 10) => ChordQuality.DominantSeventh,
            (3, 7, 10) => ChordQuality.MinorSeventh,
            (3, 6, 10) => ChordQuality.HalfDiminishedSeventh,
            (3, 6, 9) => ChordQuality.DiminishedSeventh,
            (3, 7, 11) => ChordQuality.MinorMajorSeventh,
            (4, 8, 11) => ChordQuality.AugmentedMajorSeventh,
            _ => throw new InvalidOperationException($"Unexpected chord tones {string.Join(",", t)}"),
        };
    }

    /// <summary>"minor", "dominant 7th", "half-diminished 7th" …</summary>
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
        _ => "augmented major 7th",
    };

    /// <summary>Roman numeral in the key: I, ii, vii°, V7, Imaj7, iiø7 …</summary>
    public static string Numeral(MusicKey key, int degree, bool seventh)
    {
        var quality = Quality(key, degree, seventh);
        var minorThird = ChordTones(key, degree, seventh)[1] == 3;
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
            _ => "",
        };
    }

    /// <summary>Chord symbol in the key's spelling: G7, Dm, Bdim, Cmaj7, Bm7b5 …</summary>
    public static string ChordName(MusicKey key, int degree, bool seventh)
    {
        ArgumentNullException.ThrowIfNull(key);
        var root = key.NoteName(key.PitchClass + key.DegreeOffset(degree));
        return root + Quality(key, degree, seventh) switch
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
            _ => "aug(maj7)",
        };
    }
}
