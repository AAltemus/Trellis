namespace GuitarPractice.Domain.EarTraining;

public enum EarItemKind
{
    Interval = 0,
    Chord = 1,
}

/// <summary>How the two notes of an interval are played.</summary>
public enum IntervalDirection
{
    Ascending = 0,
    Descending = 1,
    Harmonic = 2,

    /// <summary>Ascending, descending or harmonic at random, per question.</summary>
    Mixed = 3,
}

/// <summary>Where an interval starts.</summary>
public enum IntervalRoot
{
    /// <summary>Always on the key's tonic.</summary>
    Tonic = 0,

    /// <summary>On any note of the key's scale (the second note may leave the key).</summary>
    AnyScaleNote = 1,
}

/// <summary>
/// Something to recognise. Interval: <see cref="Value"/> = semitones (1–12). Chord: <see cref="Value"/> = scale degree
/// (0 = I … 6 = vii), <see cref="Seventh"/> = four-note diatonic seventh chord.
/// </summary>
public sealed record EarItem(EarItemKind Kind, int Value, bool Seventh = false)
{
    public static IReadOnlyList<EarItem> AllIntervals { get; } = [.. Enumerable.Range(1, 12).Select(s => new EarItem(EarItemKind.Interval, s))];

    public static IReadOnlyList<EarItem> AllChords { get; } =
        [.. Enumerable.Range(0, 7).Select(d => new EarItem(EarItemKind.Chord, d)), .. Enumerable.Range(0, 7).Select(d => new EarItem(EarItemKind.Chord, d, Seventh: true))];

    /// <summary>"P5", or the chord's numeral in the key ("V7").</summary>
    public string ShortLabel(MusicKey key) =>
        Kind == EarItemKind.Interval ? MusicTheory.IntervalShortName(Value) : MusicTheory.Numeral(key, Value, Seventh);

    /// <summary>"P5 · perfect 5th", "ii · Dm (minor)", "V7 · G7 (dominant 7th)".</summary>
    public string Label(MusicKey key) => $"{ShortLabel(key)} · {Detail(key)}";

    /// <summary>"perfect 5th"; for a chord its name and quality in the key: "Dm (minor)".</summary>
    public string Detail(MusicKey key) => Kind == EarItemKind.Interval
        ? MusicTheory.IntervalLongName(Value)
        : $"{MusicTheory.ChordName(key, Value, Seventh)} ({MusicTheory.QualityName(MusicTheory.Quality(key, Value, Seventh))})";

    /// <summary>
    /// Said in full: "P5: a perfect 5th"; "ii: the 2 chord in C major, Dm, a minor chord".
    /// </summary>
    public string Describe(MusicKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Kind == EarItemKind.Interval
            ? $"{ShortLabel(key)}: a {MusicTheory.IntervalLongName(Value)}"
            : $"{ShortLabel(key)}: the {Value + 1} chord{(Seventh ? " with its 7th" : "")} in {key.Name}, "
              + $"{MusicTheory.ChordName(key, Value, Seventh)}, a {MusicTheory.QualityName(MusicTheory.Quality(key, Value, Seventh))} chord";
    }

    /// <summary>
    /// Identity for all-time stats. Intervals sound the same in any key ("int:P5"); a chord's sound depends on its
    /// function and the mode, not the tonic ("major:V7", "minor:iv").
    /// </summary>
    public string StatsKey(MusicKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Kind == EarItemKind.Interval
            ? $"int:{MusicTheory.IntervalShortName(Value)}"
            : $"{(key.Mode == KeyMode.Major ? "major" : "minor")}:{MusicTheory.Numeral(key, Value, Seventh)}";
    }
}

/// <summary>One note to play, in seconds from the start of the clip.</summary>
public readonly record struct NoteEvent(double Start, int Midi, double Duration);

/// <summary>What a question sounds like: notes and the clip's length.</summary>
public sealed record EarSound(IReadOnlyList<NoteEvent> Notes)
{
    public double Length => Notes.Count == 0 ? 0 : Notes.Max(n => n.Start + n.Duration);
}

public sealed record EarQuizOptions(
    MusicKey Key,
    IReadOnlyList<EarItem> Items,
    IntervalDirection Direction = IntervalDirection.Ascending,
    IntervalRoot Root = IntervalRoot.Tonic,
    bool PlayKeyFirst = true); // intervals only; chord questions are just the chord

/// <param name="Direction">For intervals: how this one is played (never <see cref="IntervalDirection.Mixed"/>).</param>
/// <param name="RootMidi">The interval's lower note, or the chord's root.</param>
public sealed record EarQuestion(EarItem Item, IntervalDirection Direction, int RootMidi, EarSound Sound);

/// <summary>Asked / answered right the first time.</summary>
public readonly record struct EarStat(int Asked, int Correct)
{
    public double? Accuracy => Asked > 0 ? (double)Correct / Asked : null;

    public EarStat Add(bool correct) => new(Asked + 1, Correct + (correct ? 1 : 0));
}

public sealed record EarAnswer(EarItem Guess, EarItem Actual, bool IsCorrect, bool Counted);

/// <summary>
/// A practice run: picks questions from the chosen items (never the same item twice in a row when there's a choice),
/// builds what they sound like, and scores the first answer to each question (later guesses are for comparing).
/// Pure and deterministic for a given <see cref="Random"/> seed.
/// </summary>
public sealed class EarQuiz
{
    public const double KeyChordLength = 1.3;
    public const double GapAfterKey = 0.45;
    public const double MelodicNoteLength = 1.0;
    public const double LastNoteLength = 1.5;
    public const double ChordLength = 2.0;
    public const double StrumSeconds = 0.03;

    private readonly Random _random;
    private readonly Dictionary<EarItem, EarStat> _byItem = [];
    private bool _answered;

    public EarQuiz(EarQuizOptions options, Random random)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(random);
        var items = options.Items.Distinct().ToList();
        if (items.Count < 2) throw new ArgumentException("Pick at least two things to tell apart.", nameof(options));
        Options = options with { Items = items };
        _random = random;
    }

    public EarQuizOptions Options { get; }

    public EarQuestion? Current { get; private set; }

    public EarStat Score { get; private set; }

    public int Streak { get; private set; }

    public int BestStreak { get; private set; }

    public IReadOnlyDictionary<EarItem, EarStat> ByItem => _byItem;

    /// <summary>True once the current question has been answered (scored).</summary>
    public bool IsAnswered => _answered;

    public EarQuestion Next()
    {
        var candidates = Options.Items.Where(i => i != Current?.Item).ToList(); // at least 2 items: never empty
        var item = candidates[_random.Next(candidates.Count)];
        var direction = Options.Direction == IntervalDirection.Mixed
            ? (IntervalDirection)_random.Next(3)
            : Options.Direction;
        var key = Options.Key;
        var root = item.Kind == EarItemKind.Chord
            ? ChordRoot(key, item.Value)
            : key.TonicMidi + (Options.Root == IntervalRoot.AnyScaleNote ? key.DegreeOffset(_random.Next(7)) : 0);
        Current = new EarQuestion(item, direction, root, BuildSound(item, direction, root));
        _answered = false;
        return Current;
    }

    /// <summary>Scores the first answer to the current question; later answers aren't counted.</summary>
    public EarAnswer Answer(EarItem guess)
    {
        ArgumentNullException.ThrowIfNull(guess);
        var current = Current ?? throw new InvalidOperationException("No question yet.");
        var correct = guess == current.Item;
        if (_answered) return new EarAnswer(guess, current.Item, correct, Counted: false);

        _answered = true;
        Score = Score.Add(correct);
        _byItem[current.Item] = _byItem.GetValueOrDefault(current.Item).Add(correct);
        Streak = correct ? Streak + 1 : 0;
        BestStreak = Math.Max(BestStreak, Streak);
        return new EarAnswer(guess, current.Item, correct, Counted: true);
    }

    /// <summary>How <paramref name="item"/> would sound in place of the current question (same root and direction): for comparing.</summary>
    public EarSound SoundOf(EarItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var current = Current ?? throw new InvalidOperationException("No question yet.");
        var root = item.Kind == EarItemKind.Chord ? ChordRoot(Options.Key, item.Value) : current.RootMidi;
        return BuildSound(item, current.Direction, root, withKey: false);
    }

    /// <summary>Just the key's tonic chord (I or i): to re-hear the key.</summary>
    public EarSound KeySound() => new([.. Strum(TonicChord(Options.Key), 0, ChordLength)]);

    private EarSound BuildSound(EarItem item, IntervalDirection direction, int root, bool withKey = true)
    {
        var notes = new List<NoteEvent>();
        var t = 0.0;
        // Intervals only: a chord question is just the chord (user: "i dont want to go from chord to chord"); the key
        // chord is there on demand (KeySound).
        if (withKey && Options.PlayKeyFirst && item.Kind == EarItemKind.Interval)
        {
            notes.AddRange(Strum(TonicChord(Options.Key), 0, KeyChordLength));
            t = KeyChordLength + GapAfterKey;
        }

        if (item.Kind == EarItemKind.Interval)
        {
            var top = root + item.Value;
            switch (direction)
            {
                case IntervalDirection.Harmonic:
                    notes.Add(new NoteEvent(t, root, ChordLength));
                    notes.Add(new NoteEvent(t, top, ChordLength));
                    break;
                case IntervalDirection.Descending:
                    notes.Add(new NoteEvent(t, top, MelodicNoteLength));
                    notes.Add(new NoteEvent(t + MelodicNoteLength, root, LastNoteLength));
                    break;
                default:
                    notes.Add(new NoteEvent(t, root, MelodicNoteLength));
                    notes.Add(new NoteEvent(t + MelodicNoteLength, top, LastNoteLength));
                    break;
            }
        }
        else
        {
            var tones = MusicTheory.ChordTones(Options.Key, item.Value, item.Seventh);
            notes.AddRange(Strum([.. tones.Select(x => root + x)], t, ChordLength));
        }
        return new EarSound(notes);
    }

    /// <summary>The chord root on <paramref name="degree"/>, kept in C3–B3 (MIDI 48–59) so chords sit in one register.</summary>
    public static int ChordRoot(MusicKey key, int degree)
    {
        ArgumentNullException.ThrowIfNull(key);
        var root = key.TonicMidi + key.DegreeOffset(degree);
        while (root > 59) root -= 12;
        return root;
    }

    private static int[] TonicChord(MusicKey key)
    {
        var root = ChordRoot(key, 0);
        return [.. MusicTheory.ChordTones(key, 0, seventh: false).Select(x => root + x)];
    }

    /// <summary>Low to high, each note <see cref="StrumSeconds"/> after the previous; all end together.</summary>
    private static IEnumerable<NoteEvent> Strum(int[] midis, double start, double length) =>
        midis.Order().Select((m, i) => new NoteEvent(start + i * StrumSeconds, m, length - i * StrumSeconds));
}
