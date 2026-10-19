namespace GuitarPractice.Domain.EarTraining;

public sealed record MelodyOptions(MusicKey Key, int Length = 4, bool PlayKeyFirst = true);

/// <summary>A melody to write down: scale degrees (0 = the tonic … 6 = the 7th, 7 = the tonic an octave up).</summary>
public sealed record MelodyQuestion(IReadOnlyList<int> Degrees, EarSound Sound);

/// <summary>How a written-down melody compares, note by note.</summary>
public sealed record MelodyResult(IReadOnlyList<int> Entered, IReadOnlyList<bool> NoteCorrect)
{
    public int CorrectNotes => NoteCorrect.Count(c => c);

    public bool AllCorrect => NoteCorrect.All(c => c);
}

/// <summary>
/// Melodic dictation: a short melody in the key (after the key chord), written down note by note as scale degrees; the
/// melody is scored when its last note is entered (notes right, and whether all were). Melodies stay within the octave
/// from the tonic up, start on 1, 3 or 5 and move mostly by step, like a sung phrase. Pure and deterministic for a seed.
/// </summary>
public sealed class MelodyDictation
{
    public const int MinLength = 3;
    public const int MaxLength = 8;
    public const int Degrees = 8; // 1–7 and the octave
    public const double NoteLength = 0.6;
    public const double LastNoteLength = 1.2;

    // Moves (in scale steps) and how likely each is: mostly steps, some thirds, fewer leaps.
    private static readonly (int Move, int Weight)[] Moves =
        [(-1, 6), (1, 6), (-2, 3), (2, 3), (0, 1), (-3, 2), (3, 2), (-4, 1), (4, 1)];

    private static readonly int[] StartDegrees = [0, 2, 4]; // 1, 3 or 5

    private readonly Random _random;
    private readonly List<int> _entered = [];

    public MelodyDictation(MelodyOptions options, Random random)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(random);
        Options = options with { Length = Math.Clamp(options.Length, MinLength, MaxLength) };
        _random = random;
    }

    public MelodyOptions Options { get; }

    public MelodyQuestion? Current { get; private set; }

    /// <summary>Degrees written down so far for the current melody.</summary>
    public IReadOnlyList<int> Entered => _entered;

    /// <summary>Set once the last note is entered; null while writing.</summary>
    public MelodyResult? Result { get; private set; }

    /// <summary>Melodies asked (scored) / written down without a mistake.</summary>
    public EarStat Melodies { get; private set; }

    /// <summary>Notes asked / right, over the scored melodies.</summary>
    public EarStat Notes { get; private set; }

    public MelodyQuestion Next()
    {
        var degrees = new List<int> { StartDegrees[_random.Next(StartDegrees.Length)] };
        while (degrees.Count < Options.Length)
        {
            var last = degrees[^1];
            var options = Moves.Where(m => last + m.Move is >= 0 and < Degrees).ToList();
            var pick = _random.Next(options.Sum(m => m.Weight));
            foreach (var (move, weight) in options)
            {
                if (pick < weight)
                {
                    degrees.Add(last + move);
                    break;
                }
                pick -= weight;
            }
        }
        _entered.Clear();
        Result = null;
        Current = new MelodyQuestion(degrees, SoundOf(degrees, withKey: Options.PlayKeyFirst));
        return Current;
    }

    /// <summary>Writes down the next note (0–7). Returns the result when it was the last one; ignored once scored.</summary>
    public MelodyResult? Enter(int degree)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(degree);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(degree, Degrees);
        if (Current is not { } question || Result is not null) return null;
        _entered.Add(degree);
        if (_entered.Count < question.Degrees.Count) return null;

        var correct = question.Degrees.Zip(_entered, (a, b) => a == b).ToList();
        Result = new MelodyResult([.. _entered], correct);
        Melodies = Melodies.Add(Result.AllCorrect);
        Notes = new EarStat(Notes.Asked + correct.Count, Notes.Correct + Result.CorrectNotes);
        return Result;
    }

    /// <summary>Takes back the last note written (not after the melody is scored).</summary>
    public void Backspace()
    {
        if (Result is null && _entered.Count > 0) _entered.RemoveAt(_entered.Count - 1);
    }

    /// <summary>What <paramref name="degrees"/> sound like in the key: to replay the melody or hear your answer.</summary>
    public EarSound SoundOf(IReadOnlyList<int> degrees, bool withKey = false)
    {
        ArgumentNullException.ThrowIfNull(degrees);
        var notes = new List<NoteEvent>();
        var t = 0.0;
        if (withKey)
        {
            var root = EarQuiz.ChordRoot(Options.Key, 0);
            notes.AddRange(MusicTheory.ChordTones(Options.Key, 0, ChordType.Triad)
                .Select((x, i) => new NoteEvent(i * EarQuiz.StrumSeconds, root + x, EarQuiz.KeyChordLength - i * EarQuiz.StrumSeconds)));
            t = EarQuiz.KeyChordLength + EarQuiz.GapAfterKey;
        }
        for (var i = 0; i < degrees.Count; i++)
        {
            var last = i == degrees.Count - 1;
            notes.Add(new NoteEvent(t, Midi(degrees[i]), last ? LastNoteLength : NoteLength));
            t += NoteLength;
        }
        return new EarSound(notes);
    }

    /// <summary>MIDI note of a degree: from the key's tonic (E3–D#4) up to its octave.</summary>
    public int Midi(int degree) => Options.Key.TonicMidi + Options.Key.DegreeOffset(degree);

    /// <summary>"1"…"7", and "8" for the octave.</summary>
    public static string DegreeLabel(int degree) => (degree + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
