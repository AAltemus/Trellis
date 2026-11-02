using GuitarPractice.Domain.EarTraining;

namespace GuitarPractice.Domain.Tests;

public class EarTrainingTests
{
    private static readonly MusicKey CMajor = new(0, KeyMode.Major);
    private static readonly MusicKey AMinor = new(9, KeyMode.Minor);

    private static EarItem Interval(int semitones) => new(EarItemKind.Interval, semitones);

    private static EarItem Chord(int degree, bool seventh = false) =>
        new(EarItemKind.Chord, degree, seventh ? ChordType.Seventh : ChordType.Triad);

    private static string[] Names(MusicKey key, ChordType type) => // "–" where the key has no such chord
        [.. Enumerable.Range(0, 7).Select(d => MusicTheory.Exists(key, d, type) ? MusicTheory.ChordName(key, d, type) : "–")];

    [Fact]
    public void Sus_add9_sixth_and_ninth_chords_exist_only_where_the_key_makes_them()
    {
        Assert.Equal(["Csus2", "Dsus2", "–", "Fsus2", "Gsus2", "Asus2", "–"], Names(CMajor, ChordType.Sus2));
        Assert.Equal(["Csus4", "Dsus4", "Esus4", "–", "Gsus4", "Asus4", "–"], Names(CMajor, ChordType.Sus4));
        Assert.Equal(["Cadd9", "Dm(add9)", "–", "Fadd9", "Gadd9", "Am(add9)", "–"], Names(CMajor, ChordType.Add9));
        Assert.Equal(["C6", "Dm6", "–", "F6", "G6", "–", "–"], Names(CMajor, ChordType.Sixth));
        Assert.Equal(["Cmaj9", "Dm9", "–", "Fmaj9", "G9", "Am9", "–"], Names(CMajor, ChordType.Ninth));
        Assert.Equal(["Am9", "–", "Cmaj9", "Dm9", "–", "Fmaj9", "G9"], Names(AMinor, ChordType.Ninth));

        Assert.Equal(["Isus2", "iisus4", "V9", "Imaj9", "ii6", "viadd9"],
            new[] { (0, ChordType.Sus2), (1, ChordType.Sus4), (4, ChordType.Ninth), (0, ChordType.Ninth), (1, ChordType.Sixth), (5, ChordType.Add9) }
                .Select(x => MusicTheory.Numeral(CMajor, x.Item1, x.Item2)));
        Assert.Equal([0, 4, 7, 10, 14], MusicTheory.ChordTones(CMajor, 4, ChordType.Ninth));
        Assert.Throws<InvalidOperationException>(() => MusicTheory.Quality(CMajor, 3, ChordType.Sus4)); // F–B–C

        var item = new EarItem(EarItemKind.Chord, 4, ChordType.Sus4);
        Assert.Equal("Vsus4 · Gsus4 (sus4)", item.Label(CMajor));
        Assert.Equal("Vsus4: the 5 chord with a 4th for its 3rd in C major, Gsus4, a sus4 chord", item.Describe(CMajor));
        Assert.Equal("major:Vsus4", item.StatsKey(CMajor));
        Assert.Equal(7 * 7 + 2 * 2 * 7, EarItem.AllChords.Count); // 7 types × 7 degrees + borrowed/harmonic triads and 7ths
    }

    [Fact]
    public void A_quiz_leaves_out_chords_the_key_doesnt_have_and_plays_every_tone()
    {
        var items = new[] { new EarItem(EarItemKind.Chord, 3, ChordType.Sus4), new EarItem(EarItemKind.Chord, 0, ChordType.Ninth),
            new EarItem(EarItemKind.Chord, 4, ChordType.Ninth), new EarItem(EarItemKind.Chord, 1, ChordType.Sixth) };
        var quiz = new EarQuiz(new EarQuizOptions(CMajor, items), new Random(3));
        Assert.Equal(3, quiz.Options.Items.Count);                                    // IVsus4 isn't in C major
        for (var i = 0; i < 20; i++)
        {
            var q = quiz.Next();
            Assert.Equal(MusicTheory.ChordTones(CMajor, q.Item.Value, q.Item.Chord), q.Sound.Notes.Select(n => n.Midi - q.RootMidi));
        }
    }

    [Fact]
    public void Diatonic_chords_in_major_and_minor()
    {
        Assert.Equal(["I", "ii", "iii", "IV", "V", "vi", "vii°"], Enumerable.Range(0, 7).Select(d => MusicTheory.Numeral(CMajor, d, false)));
        Assert.Equal(["Imaj7", "ii7", "iii7", "IVmaj7", "V7", "vi7", "viiø7"], Enumerable.Range(0, 7).Select(d => MusicTheory.Numeral(CMajor, d, true)));
        Assert.Equal(["C", "Dm", "Em", "F", "G", "Am", "Bdim"], Enumerable.Range(0, 7).Select(d => MusicTheory.ChordName(CMajor, d, false)));
        Assert.Equal(["Cmaj7", "Dm7", "Em7", "Fmaj7", "G7", "Am7", "Bm7b5"], Enumerable.Range(0, 7).Select(d => MusicTheory.ChordName(CMajor, d, true)));

        Assert.Equal(["i", "ii°", "III", "iv", "v", "VI", "VII"], Enumerable.Range(0, 7).Select(d => MusicTheory.Numeral(AMinor, d, false)));
        Assert.Equal(["Am7", "Bm7b5", "Cmaj7", "Dm7", "Em7", "Fmaj7", "G7"], Enumerable.Range(0, 7).Select(d => MusicTheory.ChordName(AMinor, d, true)));
        Assert.Equal([0, 4, 7, 10], MusicTheory.ChordTones(CMajor, 4, seventh: true));
    }

    [Fact]
    public void Keys_spell_with_sharps_or_flats_and_sit_in_a_guitar_octave()
    {
        Assert.Equal(24, MusicKey.All.Distinct().Count());
        Assert.Equal("Bb major", new MusicKey(10, KeyMode.Major).Name);
        Assert.Equal("F# major", new MusicKey(6, KeyMode.Major).Name);
        Assert.Equal("C minor", new MusicKey(0, KeyMode.Minor).Name);
        Assert.Equal("Eb", MusicTheory.ChordName(new MusicKey(0, KeyMode.Minor), 2, false));     // III in C minor
        Assert.Equal("Bbmaj7", MusicTheory.ChordName(new MusicKey(5, KeyMode.Major), 3, true)); // IVmaj7 in F
        Assert.All(MusicKey.All, k => Assert.InRange(k.TonicMidi, 52, 63));
        Assert.Equal(52, new MusicKey(4, KeyMode.Minor).TonicMidi);                             // E3
    }

    [Fact]
    public void Interval_names()
    {
        Assert.Equal(["m2", "M2", "m3", "M3", "P4", "TT", "P5", "m6", "M6", "m7", "M7", "P8"], EarItem.AllIntervals.Select(i => i.ShortLabel(CMajor)));
        Assert.Equal("P5 · perfect 5th", Interval(7).Label(CMajor));
        Assert.Equal("V7 · E7 (dominant 7th)", Chord(4, true).Label(new MusicKey(9, KeyMode.Major)));
        Assert.Equal("ii · Dm (minor)", Chord(1).Label(CMajor));
        Assert.Equal("viiø7 · Bm7b5 (half-diminished 7th)", Chord(6, true).Label(CMajor));
    }

    [Fact]
    public void Answers_are_described_by_degree_and_quality()
    {
        Assert.Equal("ii: the 2 chord in C major, Dm, a minor chord", Chord(1).Describe(CMajor));
        Assert.Equal("V7: the 5 chord with its 7th in G major, D7, a dominant 7th chord", Chord(4, true).Describe(new MusicKey(7, KeyMode.Major)));
        Assert.Equal("iv: the 4 chord in A minor, Dm, a minor chord", Chord(3).Describe(AMinor));
        Assert.Equal("P5: a perfect 5th", Interval(7).Describe(CMajor));
    }

    [Fact]
    public void A_chord_question_is_just_the_chord_even_with_key_first_on()
    {
        var quiz = new EarQuiz(new EarQuizOptions(CMajor, [Chord(1), Chord(4)], PlayKeyFirst: true), new Random(1));
        var q = quiz.Next();

        Assert.Equal(3, q.Sound.Notes.Count);
        Assert.Equal(0, q.Sound.Notes[0].Start);
        Assert.Equal(MusicTheory.ChordTones(CMajor, q.Item.Value, false), q.Sound.Notes.Select(n => n.Midi - q.RootMidi));
        Assert.Equal([48, 52, 55], quiz.KeySound().Notes.Select(n => n.Midi)); // the key is still there on demand
    }

    [Fact]
    public void Stats_keys_ignore_the_tonic_but_not_the_mode()
    {
        Assert.Equal("int:P5", Interval(7).StatsKey(AMinor));
        Assert.Equal("major:V7", Chord(4, true).StatsKey(new MusicKey(7, KeyMode.Major)));
        Assert.Equal(Chord(3).StatsKey(CMajor), Chord(3).StatsKey(new MusicKey(2, KeyMode.Major)));
        Assert.Equal("minor:iv", Chord(3).StatsKey(AMinor));
    }

    [Fact]
    public void Ascending_interval_from_the_tonic_after_the_key_chord()
    {
        var quiz = new EarQuiz(new EarQuizOptions(CMajor, [Interval(7), Interval(4)]), new Random(1));
        var q = quiz.Next();

        Assert.Equal(60, CMajor.TonicMidi); // C4
        Assert.Equal(60, q.RootMidi);
        Assert.Equal([48, 52, 55], q.Sound.Notes.Take(3).Select(n => n.Midi)); // C major triad, kept in C3–B3
        var target = q.Sound.Notes.Skip(3).ToList();
        Assert.Equal([60, 60 + q.Item.Value], target.Select(n => n.Midi));
        Assert.Equal(EarQuiz.KeyChordLength + EarQuiz.GapAfterKey, target[0].Start, 9);
        Assert.True(target[1].Start > target[0].Start);
    }

    [Fact]
    public void Descending_harmonic_and_mixed_intervals()
    {
        EarQuestion Ask(IntervalDirection d, int seed = 1) =>
            new EarQuiz(new EarQuizOptions(CMajor, [Interval(3), Interval(9)], d, PlayKeyFirst: false), new Random(seed)).Next();

        var down = Ask(IntervalDirection.Descending);
        Assert.Equal([down.RootMidi + down.Item.Value, down.RootMidi], down.Sound.Notes.Select(n => n.Midi));

        var together = Ask(IntervalDirection.Harmonic);
        Assert.Equal(together.Sound.Notes[0].Start, together.Sound.Notes[1].Start);

        var mixed = Enumerable.Range(0, 40).Select(s => Ask(IntervalDirection.Mixed, s).Direction).Distinct().Order();
        Assert.Equal([IntervalDirection.Ascending, IntervalDirection.Descending, IntervalDirection.Harmonic], mixed);
    }

    [Fact]
    public void Any_scale_note_roots_stay_in_the_key()
    {
        var quiz = new EarQuiz(new EarQuizOptions(AMinor, [Interval(5), Interval(7)], Root: IntervalRoot.AnyScaleNote), new Random(3));
        var roots = Enumerable.Range(0, 60).Select(_ => quiz.Next().RootMidi % 12).Distinct().Order().ToList();
        Assert.Equal([0, 2, 4, 5, 7, 9, 11], roots); // A natural minor = the white keys
    }

    [Fact]
    public void Chords_are_strummed_low_to_high_in_one_register()
    {
        var quiz = new EarQuiz(new EarQuizOptions(CMajor, [Chord(4, true), Chord(1)], PlayKeyFirst: false), new Random(2));
        for (var i = 0; i < 10; i++)
        {
            var q = quiz.Next();
            var notes = q.Sound.Notes;
            Assert.InRange(q.RootMidi, 48, 59);
            Assert.Equal(q.Item.Seventh ? 4 : 3, notes.Count);
            Assert.Equal(notes.OrderBy(n => n.Midi).Select(n => n.Start), notes.Select(n => n.Start));
            Assert.Equal(MusicTheory.ChordTones(CMajor, q.Item.Value, q.Item.Seventh), notes.Select(n => n.Midi - q.RootMidi));
            Assert.All(notes, n => Assert.Equal(EarQuiz.ChordLength, n.Start + n.Duration, 9));
        }
    }

    [Fact]
    public void Never_the_same_item_twice_in_a_row_and_every_item_comes_up()
    {
        var items = new[] { Chord(0), Chord(3), Chord(4) };
        var quiz = new EarQuiz(new EarQuizOptions(CMajor, items), new Random(7));
        var asked = Enumerable.Range(0, 60).Select(_ => quiz.Next().Item).ToList();

        Assert.All(asked.Zip(asked.Skip(1)), p => Assert.NotEqual(p.First, p.Second));
        Assert.Equal(items.ToHashSet(), asked.ToHashSet());
    }

    [Fact]
    public void Only_the_first_answer_counts_and_streaks_are_tracked()
    {
        var quiz = new EarQuiz(new EarQuizOptions(CMajor, [Interval(7), Interval(5)]), new Random(5));
        EarItem Other(EarItem item) => Interval(item.Value == 7 ? 5 : 7);

        var q = quiz.Next();
        Assert.True(quiz.Answer(q.Item).IsCorrect);
        Assert.False(quiz.Answer(Other(q.Item)).Counted); // comparing afterwards doesn't count

        q = quiz.Next();
        Assert.True(quiz.Answer(q.Item).Counted);
        q = quiz.Next();
        var wrong = quiz.Answer(Other(q.Item));
        Assert.Equal((false, q.Item), (wrong.IsCorrect, wrong.Actual));
        quiz.Answer(q.Item);                              // right on the second try: not scored

        Assert.Equal(new EarStat(3, 2), quiz.Score);
        Assert.Equal((0, 2), (quiz.Streak, quiz.BestStreak));
        Assert.Equal(3, quiz.ByItem.Values.Sum(s => s.Asked));
        Assert.Equal(2.0 / 3, quiz.Score.Accuracy!.Value, 9);
    }

    [Fact]
    public void Comparing_plays_the_guess_on_the_same_root_without_the_key_chord()
    {
        var quiz = new EarQuiz(new EarQuizOptions(CMajor, [Interval(7), Interval(5)], IntervalDirection.Descending), new Random(1));
        var q = quiz.Next();

        var compare = quiz.SoundOf(Interval(5));
        Assert.Equal([q.RootMidi + 5, q.RootMidi], compare.Notes.Select(n => n.Midi));
        Assert.Equal(0, compare.Notes[0].Start);
        Assert.Equal([48, 52, 55], quiz.KeySound().Notes.Select(n => n.Midi));
    }

    [Fact]
    public void Needs_at_least_two_different_items()
    {
        Assert.Throws<ArgumentException>(() => new EarQuiz(new EarQuizOptions(CMajor, [Chord(0), Chord(0)]), new Random(1)));
        Assert.Throws<InvalidOperationException>(() => new EarQuiz(new EarQuizOptions(CMajor, [Chord(0), Chord(1)]), new Random(1)).Answer(Chord(0)));
    }

    [Fact]
    public void Every_diatonic_chord_in_every_key_has_a_name()
    {
        foreach (var key in MusicKey.All)
            foreach (var item in EarItem.AllChords.Where(i => i.ExistsIn(key)))
            {
                Assert.False(string.IsNullOrEmpty(item.Label(key)));
                Assert.InRange(EarQuiz.ChordRoot(key, item.Value, item.Source), 48, 59);
            }
    }

    private static string[] Names(MusicKey key, ChordType type, ChordSource source) =>
        [.. Enumerable.Range(0, 7).Select(d => MusicTheory.Exists(key, d, type, source) ? MusicTheory.ChordName(key, d, type, source) : "–")];

    private static string[] Numerals(MusicKey key, ChordType type, ChordSource source) =>
        [.. Enumerable.Range(0, 7).Select(d => MusicTheory.Exists(key, d, type, source) ? MusicTheory.Numeral(key, d, type, source) : "–")];

    [Fact]
    public void Borrowed_chords_come_from_the_parallel_key_and_only_where_they_differ()
    {
        Assert.Equal(["Cm", "Ddim", "Eb", "Fm", "Gm", "Ab", "Bb"], Names(CMajor, ChordType.Triad, ChordSource.Borrowed));
        Assert.Equal(["i", "ii°", "♭III", "iv", "v", "♭VI", "♭VII"], Numerals(CMajor, ChordType.Triad, ChordSource.Borrowed));
        Assert.Equal(["i7", "iiø7", "♭IIImaj7", "iv7", "v7", "♭VImaj7", "♭VII7"], Numerals(CMajor, ChordType.Seventh, ChordSource.Borrowed));
        Assert.Equal("Ab7", MusicTheory.ChordName(new MusicKey(10, KeyMode.Major), 6, ChordType.Seventh, ChordSource.Borrowed)); // ♭VII7 in Bb
        Assert.Equal("C", MusicTheory.ChordName(new MusicKey(4, KeyMode.Major), 5, ChordType.Triad, ChordSource.Borrowed)); // ♭VI in E: C, not B#

        // In minor: the major key's chords, but not V or vii° (the harmonic minor's, and the same notes).
        Assert.Equal(["A", "Bm", "C#m", "D", "–", "F#m", "–"], Names(AMinor, ChordType.Triad, ChordSource.Borrowed));
        Assert.Equal(["I", "ii", "♮iii", "IV", "–", "♮vi", "–"], Numerals(AMinor, ChordType.Triad, ChordSource.Borrowed));
        Assert.Equal(["Imaj7", "ii7", "♮iii7", "IVmaj7", "–", "♮vi7", "♮viiø7"], Numerals(AMinor, ChordType.Seventh, ChordSource.Borrowed));

        // Only triads and sevenths.
        Assert.False(MusicTheory.Exists(CMajor, 5, ChordType.Sus2, ChordSource.Borrowed));
        Assert.Equal("♭VI: the 6 chord in C major, borrowed from C minor, Ab, a major chord",
            new EarItem(EarItemKind.Chord, 5, ChordType.Triad, ChordSource.Borrowed).Describe(CMajor));
    }

    [Fact]
    public void Harmonic_minor_gives_minor_keys_V_V7_and_the_leading_tone_chords()
    {
        Assert.Equal(["–", "–", "Caug", "–", "E", "–", "G#dim"], Names(AMinor, ChordType.Triad, ChordSource.HarmonicMinor));
        Assert.Equal(["–", "–", "III+", "–", "V", "–", "vii°"], Numerals(AMinor, ChordType.Triad, ChordSource.HarmonicMinor));
        Assert.Equal(["Am(maj7)", "–", "Caug(maj7)", "–", "E7", "–", "G#dim7"], Names(AMinor, ChordType.Seventh, ChordSource.HarmonicMinor));
        Assert.Equal(["i(maj7)", "–", "III+maj7", "–", "V7", "–", "vii°7"], Numerals(AMinor, ChordType.Seventh, ChordSource.HarmonicMinor));
        var dMinor = new MusicKey(2, KeyMode.Minor);
        Assert.Equal("C#dim", MusicTheory.ChordName(dMinor, 6, ChordType.Triad, ChordSource.HarmonicMinor)); // not Dbdim
        Assert.Equal("A7", MusicTheory.ChordName(dMinor, 4, ChordType.Seventh, ChordSource.HarmonicMinor));
        Assert.All(Enumerable.Range(0, 7), d => Assert.False(MusicTheory.Exists(CMajor, d, ChordType.Triad, ChordSource.HarmonicMinor)));
    }

    [Fact]
    public void Borrowed_and_harmonic_chords_sound_from_their_own_root_and_keep_their_own_stats()
    {
        var flatSix = new EarItem(EarItemKind.Chord, 5, ChordType.Triad, ChordSource.Borrowed);
        var quiz = new EarQuiz(new EarQuizOptions(CMajor, [flatSix, Chord(5)]), new Random(3));
        for (var i = 0; i < 10; i++)
        {
            var q = quiz.Next();
            Assert.Equal(q.Item == flatSix ? 56 : 57, q.RootMidi);                 // Ab3 or A3
            Assert.Equal(q.Item == flatSix ? [0, 4, 7] : new[] { 0, 3, 7 }, q.Sound.Notes.Select(n => n.Midi - q.RootMidi));
        }

        foreach (var key in MusicKey.All)
        {
            var keys = EarItem.AllChords.Where(i => i.ExistsIn(key)).Select(i => i.StatsKey(key)).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
        }
    }

    [Fact]
    public void Favoring_weak_items_asks_the_missed_ones_more_often()
    {
        Assert.Equal(0.75, EarQuiz.Weight(default), 3);
        Assert.InRange(EarQuiz.Weight(new EarStat(20, 20)), 0.29, 0.30);
        Assert.InRange(EarQuiz.Weight(new EarStat(5, 0)), 1.10, 1.11);

        var items = new[] { Interval(3), Interval(4), Interval(7) };
        var history = new Dictionary<string, EarStat>
        {
            [Interval(3).StatsKey(CMajor)] = new(10, 1),
            [Interval(4).StatsKey(CMajor)] = new(10, 10),
            [Interval(7).StatsKey(CMajor)] = new(10, 10),
        };
        foreach (var favor in new[] { true, false })
        {
            var quiz = new EarQuiz(new EarQuizOptions(CMajor, items, FavorWeak: favor, History: history), new Random(7));
            var counts = new Dictionary<int, int>();
            EarItem? last = null;
            for (var i = 0; i < 3_000; i++)
            {
                var q = quiz.Next();
                Assert.NotEqual(last, q.Item);                                     // still never twice in a row
                last = q.Item;
                counts[q.Item.Value] = counts.GetValueOrDefault(q.Item.Value) + 1;
            }
            // Never twice in a row caps it at every other question (2×); uniform would be 1×.
            if (favor) Assert.True(counts[3] > 1.3 * counts[4], $"m3 {counts[3]} vs M3 {counts[4]}");
            else Assert.InRange(counts[3], 900, 1_100);
        }

        // Without a history the run's own results count: missing one makes it come up more.
        var own = new EarQuiz(new EarQuizOptions(CMajor, items, FavorWeak: true), new Random(1));
        for (var i = 0; i < 300; i++)
        {
            var q = own.Next();
            own.Answer(q.Item.Value == 3 ? Interval(4) : q.Item);
        }
        Assert.True(own.ByItem[Interval(3)].Asked > own.ByItem[Interval(4)].Asked);
    }
}
