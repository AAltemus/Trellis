using GuitarPractice.Audio;

namespace GuitarPractice.Audio.Tests;

public class BendTrackerTests
{
    private const int G3 = 55;
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(20);

    private readonly BendTracker _tracker = new() { IntervalSemitones = 2 };
    private TimeSpan _now;

    private static double Hz(double midi) => Pitch.FrequencyFromMidi(midi);

    /// <summary>Feeds a constant pitch (in fractional MIDI, null = silence) for a duration at 50 frames/s.</summary>
    private List<BendState> Play(double? midi, TimeSpan duration)
    {
        var states = new List<BendState>();
        for (var t = TimeSpan.Zero; t < duration; t += Frame)
        {
            states.Add(_tracker.Update(midi is { } m ? Hz(m) : null, _now));
            _now += Frame;
        }
        return states;
    }

    /// <summary>Glides linearly from one pitch to another.</summary>
    private List<BendState> Glide(double fromMidi, double toMidi, TimeSpan duration)
    {
        var states = new List<BendState>();
        var frames = (int)(duration / Frame);
        for (var i = 0; i < frames; i++)
        {
            states.Add(_tracker.Update(Hz(fromMidi + (toMidi - fromMidi) * i / frames), _now));
            _now += Frame;
        }
        return states;
    }

    private static BendAttempt Completed(IEnumerable<BendState> states) => Assert.Single(states, s => s.Completed is not null).Completed!;

    [Fact]
    public void First_note_sets_the_reference_and_target()
    {
        var s = Play(G3 + 0.1, Frame)[0]; // fretted slightly sharp
        Assert.Equal((G3, G3 + 2), (s.ReferenceMidi, s.TargetMidi));
        Assert.Equal(BendPhase.Unbent, s.Phase);
        Assert.InRange(s.CentsFromTarget, -191, -189);
    }

    [Fact]
    public void Phases_follow_the_bend()
    {
        Play(G3, TimeSpan.FromMilliseconds(100));
        Assert.Equal(BendPhase.Bending, Play(G3 + 1, Frame)[0].Phase);
        var hold = Play(G3 + 2.1, Frame)[0];
        Assert.Equal(BendPhase.Holding, hold.Phase);
        Assert.InRange(hold.CentsFromTarget, 9, 11);
        Assert.InRange(hold.Progress, 1.04, 1.06);
        Assert.Equal(BendPhase.Overbent, Play(G3 + 2.7, Frame)[0].Phase);
    }

    [Fact]
    public void A_held_bend_is_scored_once_on_release()
    {
        Play(G3, TimeSpan.FromMilliseconds(200));
        Glide(G3, G3 + 2.08, TimeSpan.FromMilliseconds(200));
        var held = Play(G3 + 2.08, TimeSpan.FromMilliseconds(600)); // +8 ¢
        Assert.All(held, s => Assert.Null(s.Completed));

        var release = Glide(G3 + 2.08, G3, TimeSpan.FromMilliseconds(200));
        var attempt = Completed(release);
        Assert.Equal((G3, G3 + 2), (attempt.ReferenceMidi, attempt.TargetMidi));
        Assert.InRange(attempt.AverageCents, 7.5, 8.5); // the glide in and out is trimmed off
        Assert.InRange(attempt.Held.TotalMilliseconds, 580, 760);
    }

    [Fact]
    public void Silence_while_holding_scores_and_resets()
    {
        Play(G3, TimeSpan.FromMilliseconds(100));
        Play(G3 + 1.9, TimeSpan.FromMilliseconds(400)); // −10 ¢ (jumped straight there)
        var stop = Play(null, TimeSpan.FromMilliseconds(500)); // longer than GapTolerance
        var attempt = Completed(stop);
        Assert.InRange(attempt.AverageCents, -10.5, -9.5);
        Assert.Equal(BendPhase.Holding, stop[0].Phase);  // a brief gap keeps the last state on screen...
        Assert.Equal(BendPhase.Idle, stop[^1].Phase);    // ...then it resets
        Assert.Null(stop[^1].ReferenceMidi);
    }

    [Fact]
    public void Vibrato_on_the_held_bend_averages_out()
    {
        Play(G3, TimeSpan.FromMilliseconds(100));
        var states = new List<BendState>();
        for (var i = 0; i < 50; i++) // 1 s of ±20 ¢ vibrato at 5 Hz around +4 ¢
        {
            states.Add(_tracker.Update(Hz(G3 + 2.04 + 0.2 * Math.Sin(2 * Math.PI * 5 * i * 0.02)), _now));
            _now += Frame;
        }
        Assert.All(states, s => Assert.Equal(BendPhase.Holding, s.Phase));
        Assert.InRange(Completed(Play(null, TimeSpan.FromMilliseconds(500))).AverageCents, 1, 7);
    }

    [Fact]
    public void Too_short_a_hold_is_not_scored()
    {
        Play(G3, TimeSpan.FromMilliseconds(100));
        Play(G3 + 2, TimeSpan.FromMilliseconds(100));
        Assert.All(Play(null, Frame).Concat(Play(G3, Frame)), s => Assert.Null(s.Completed));
    }

    [Fact]
    public void Overbending_out_of_the_window_ends_the_hold()
    {
        Play(G3, TimeSpan.FromMilliseconds(100));
        Play(G3 + 2, TimeSpan.FromMilliseconds(300));
        var over = Play(G3 + 2.8, Frame);
        Assert.Equal(BendPhase.Overbent, over[0].Phase);
        Assert.InRange(Completed(over).AverageCents, -0.5, 0.5);
    }

    [Fact]
    public void Repeated_bends_from_the_same_note_are_scored_separately()
    {
        Play(G3, TimeSpan.FromMilliseconds(100));
        var all = new List<BendState>();
        foreach (var offset in new[] { 0.05, -0.12 })
        {
            all.AddRange(Play(G3 + 2 + offset, TimeSpan.FromMilliseconds(300)));
            all.AddRange(Play(G3, TimeSpan.FromMilliseconds(100)));
        }
        var attempts = all.Where(s => s.Completed is not null).Select(s => s.Completed!.AverageCents).ToList();
        Assert.Equal(2, attempts.Count);
        Assert.InRange(attempts[0], 4.5, 5.5);
        Assert.InRange(attempts[1], -12.5, -11.5);
    }

    [Fact]
    public void A_lower_fretted_note_or_silence_gives_a_new_reference()
    {
        Play(G3, Frame);
        Assert.Equal(G3 - 2, Play(G3 - 2, Frame)[0].ReferenceMidi); // moved down two frets
        Play(null, TimeSpan.FromMilliseconds(500));
        Assert.Equal(G3 + 5, Play(G3 + 5, Frame)[0].ReferenceMidi); // new note after silence
    }

    [Fact]
    public void Short_dropouts_mid_bend_keep_the_reference_and_pause_the_hold()
    {
        Play(G3, TimeSpan.FromMilliseconds(200));
        var all = new List<BendState>();
        all.AddRange(Play(G3 + 1, TimeSpan.FromMilliseconds(60)));
        all.AddRange(Play(null, TimeSpan.FromMilliseconds(120)));          // pitch track blinks out mid-bend
        all.AddRange(Play(G3 + 2.03, TimeSpan.FromMilliseconds(400)));
        all.AddRange(Play(null, TimeSpan.FromMilliseconds(200)));          // and again while holding
        all.AddRange(Play(G3 + 2.03, TimeSpan.FromMilliseconds(400)));
        all.AddRange(Play(G3, TimeSpan.FromMilliseconds(100)));            // release

        Assert.All(all, s => Assert.Equal(G3, s.ReferenceMidi));           // never re-referenced to the bent pitch
        var attempt = Completed(all);
        Assert.InRange(attempt.AverageCents, 2.5, 3.5);
        Assert.InRange(attempt.Held.TotalMilliseconds, 740, 820);         // the gaps don't count as held
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void Interval_sets_the_target(int semitones)
    {
        _tracker.IntervalSemitones = semitones;
        var s = Play(G3, Frame)[0];
        Assert.Equal(G3 + semitones, s.TargetMidi);
        Assert.Equal(BendPhase.Holding, Play(G3 + semitones, Frame)[0].Phase);
    }
}
