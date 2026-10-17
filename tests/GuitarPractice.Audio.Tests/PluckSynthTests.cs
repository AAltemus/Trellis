using GuitarPractice.Audio;

namespace GuitarPractice.Audio.Tests;

public class PluckSynthTests
{
    private const int Rate = 48_000;

    private static double Rms(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (var v in x) sum += v * (double)v;
        return Math.Sqrt(sum / x.Length);
    }

    [Theory]
    [InlineData(40)] // E2, low E string
    [InlineData(45)]
    [InlineData(52)]
    [InlineData(57)]
    [InlineData(61)]
    [InlineData(64)]
    [InlineData(69)] // A4
    [InlineData(71)]
    [InlineData(76)]
    [InlineData(83)]
    public void Every_note_is_in_tune(int midi)
    {
        var audio = PluckSynth.Render(Rate, [new SynthNote(0, midi, 1.0)]);
        var yin = new YinPitchDetector(Rate);
        var window = audio.AsSpan(Rate / 5, yin.RequiredSamples); // 200 ms in: past the pick noise

        var estimate = yin.Detect(window);
        var (nearest, cents) = Pitch.Nearest(estimate.Hz);
        Assert.Equal(midi, nearest);
        Assert.InRange(cents, -2, 2);
    }

    [Fact]
    public void Notes_decay_and_high_notes_die_sooner()
    {
        double Ratio(int midi)
        {
            var a = PluckSynth.Render(Rate, [new SynthNote(0, midi, 2.0)]);
            return Rms(a.AsSpan(Rate * 3 / 2, Rate / 4)) / Rms(a.AsSpan(Rate / 20, Rate / 4));
        }
        var low = Ratio(45);
        var high = Ratio(76);
        Assert.InRange(low, 0.01, 0.9);
        Assert.True(high < low);
    }

    [Fact]
    public void Clip_is_normalised_timed_and_deterministic()
    {
        SynthNote[] notes = [new(0, 48, 1.0), new(0.03, 52, 0.97), new(0.06, 55, 0.94)];
        var chord = PluckSynth.Render(Rate, notes);

        Assert.Equal(Rate, chord.Length);
        Assert.Equal(PluckSynth.Peak, chord.Max(Math.Abs), 5);
        Assert.Equal(chord, PluckSynth.Render(Rate, notes));
        Assert.Equal(0f, chord[0]);
        Assert.InRange(Math.Abs(chord[^1]), 0f, 1e-3f);                     // released to silence

        var late = PluckSynth.Render(Rate, [new SynthNote(0.5, 60, 0.5)]);
        Assert.All(late.AsSpan(0, Rate / 2).ToArray(), v => Assert.Equal(0f, v)); // starts on time
        Assert.True(Rms(late.AsSpan(Rate / 2, 2_000)) > 0.05);
        Assert.Empty(PluckSynth.Render(Rate, []));
    }

    [Fact]
    public void Player_plays_once_then_silence_and_replay_restarts()
    {
        var player = new ClipPlayer();
        var clip = Enumerable.Range(1, 300).Select(i => i / 1000f).ToArray();
        var buffer = new float[256];

        player.Render(buffer);
        Assert.All(buffer, v => Assert.Equal(0f, v)); // nothing yet

        player.Play(clip);
        player.Render(buffer);
        Assert.Equal(clip.AsSpan(0, 256).ToArray(), buffer);
        Assert.True(player.IsPlaying);
        player.Render(buffer);
        Assert.Equal(clip.AsSpan(256).ToArray(), buffer.AsSpan(0, 44).ToArray());
        Assert.All(buffer.AsSpan(44).ToArray(), v => Assert.Equal(0f, v));
        Assert.False(player.IsPlaying);

        player.Play(clip);                             // same clip again: from the start
        player.Volume = 0.5f;
        player.Render(buffer);
        Assert.Equal(clip[0] * 0.5f, buffer[0]);

        player.Stop();
        player.Render(buffer);
        Assert.All(buffer, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Player_render_does_not_allocate()
    {
        var player = new ClipPlayer();
        player.Play(new float[Rate * 30]); // longer than warm-up + measurement: still playing while measured
        var buffer = new float[256];
        Allocations.AssertNone(() => player.Render(buffer));
    }
}
