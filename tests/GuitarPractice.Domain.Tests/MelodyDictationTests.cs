using GuitarPractice.Domain.EarTraining;

namespace GuitarPractice.Domain.Tests;

public class MelodyDictationTests
{
    private static readonly MusicKey CMajor = new(0, KeyMode.Major);
    private static readonly int[] StartDegrees = [0, 2, 4];

    [Fact]
    public void Melodies_have_the_chosen_length_stay_in_the_octave_and_move_mostly_by_step()
    {
        var dictation = new MelodyDictation(new MelodyOptions(CMajor, Length: 6), new Random(1));
        var moves = new List<int>();
        for (var i = 0; i < 300; i++)
        {
            var q = dictation.Next();
            Assert.Equal(6, q.Degrees.Count);
            Assert.Contains(q.Degrees[0], StartDegrees);
            Assert.All(q.Degrees, d => Assert.InRange(d, 0, 7));
            moves.AddRange(q.Degrees.Zip(q.Degrees.Skip(1), (a, b) => Math.Abs(b - a)));
        }
        Assert.All(moves, m => Assert.InRange(m, 0, 4));
        Assert.True(moves.Count(m => m <= 2) > moves.Count * 0.7);                 // singable: mostly steps and thirds
        Assert.Equal(MelodyDictation.MaxLength, new MelodyDictation(new MelodyOptions(CMajor, 20), new Random(1)).Options.Length);
    }

    [Fact]
    public void The_sound_is_the_key_chord_then_the_melody_in_the_key()
    {
        var dictation = new MelodyDictation(new MelodyOptions(CMajor, Length: 4), new Random(2));
        var q = dictation.Next();
        var tonic = CMajor.TonicMidi; // C4 = 60
        var melody = q.Sound.Notes.Skip(3).ToList();                               // after the 3-note key chord
        Assert.Equal(q.Degrees.Select(d => tonic + CMajor.DegreeOffset(d)), melody.Select(n => n.Midi));
        Assert.True(melody[0].Start >= EarQuiz.KeyChordLength);
        Assert.Equal(72, dictation.Midi(7));                                       // the octave
        Assert.Equal(q.Degrees.Count, dictation.SoundOf(q.Degrees).Notes.Count);   // without the key
    }

    [Fact]
    public void Writing_it_down_scores_each_note_when_the_last_one_is_entered()
    {
        var dictation = new MelodyDictation(new MelodyOptions(CMajor, Length: 3), new Random(5));
        var q = dictation.Next();
        Assert.Null(dictation.Enter(q.Degrees[0]));
        dictation.Enter((q.Degrees[1] + 1) % 8);                                   // a mistake…
        dictation.Backspace();                                                     // …taken back
        Assert.Single(dictation.Entered);
        dictation.Enter((q.Degrees[1] + 1) % 8);                                   // wrong again
        var result = dictation.Enter(q.Degrees[2]);

        Assert.NotNull(result);
        Assert.Equal([true, false, true], result.NoteCorrect);
        Assert.False(result.AllCorrect);
        Assert.Null(dictation.Enter(0));                                           // ignored once scored
        dictation.Backspace();
        Assert.Equal(3, dictation.Entered.Count);

        var second = dictation.Next();
        Assert.Empty(dictation.Entered);
        foreach (var d in second.Degrees) dictation.Enter(d);
        Assert.True(dictation.Result!.AllCorrect);
        Assert.Equal((new EarStat(2, 1), new EarStat(6, 5)), (dictation.Melodies, dictation.Notes));
        Assert.Throws<ArgumentOutOfRangeException>(() => dictation.Enter(8));
    }
}
