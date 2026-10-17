using GuitarPractice.Audio;

namespace GuitarPractice.Audio.Tests;

public class DrumTests
{
    private const int Rate = 48_000;

    private static float[] Render(MetronomeSettings settings, int samples)
    {
        var engine = new MetronomeEngine(Rate, settings);
        engine.Start();
        var output = new float[samples];
        for (var i = 0; i < samples; i += 256) engine.Render(output.AsSpan(i, Math.Min(256, samples - i)));
        return output;
    }

    private static DrumPattern Pattern(string steps, int beats = 4, Subdivision sub = Subdivision.Quarters) =>
        DrumPattern.Parse("Test", beats, sub, steps);

    private static MetronomeSettings Drums(DrumPattern pattern, double bpm = 120) =>
        new(Bpm: bpm, BeatsPerBar: pattern.BeatsPerBar, Subdivision: pattern.Subdivision, Sound: MetronomeSound.Drums, Drums: pattern);

    private static double Rms(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (var v in x) sum += v * (double)v;
        return Math.Sqrt(sum / x.Length);
    }

    /// <summary>Fraction of energy above ~5 kHz, from first differences (a crude but monotonic brightness measure).</summary>
    private static double Brightness(float[] x)
    {
        double diff = 0, total = 0;
        for (var i = 1; i < x.Length; i++)
        {
            diff += Math.Pow(x[i] - x[i - 1], 2);
            total += x[i] * (double)x[i];
        }
        return diff / total;
    }

    [Fact]
    public void Each_drum_is_its_own_sound_kick_low_hat_bright()
    {
        var kick = DrumSynth.Kick(Rate);
        var snare = DrumSynth.Snare(Rate);
        var hat = DrumSynth.HiHat(Rate);

        Assert.Equal((DrumSynth.KickPeak, DrumSynth.SnarePeak, DrumSynth.HiHatPeak),
            (kick.Max(Math.Abs), snare.Max(Math.Abs), hat.Max(Math.Abs)));
        Assert.True(Brightness(kick) < Brightness(snare));
        Assert.True(Brightness(snare) < Brightness(hat));
        Assert.True(kick.Length > snare.Length && snare.Length > hat.Length);
        foreach (var sound in new[] { kick, snare, hat })
            Assert.Equal((0f, 0f), (sound[0], sound[^1])); // no clicks at the edges
        Assert.Equal(kick, DrumSynth.Kick(Rate));          // deterministic
    }

    [Fact]
    public void Drums_play_only_on_their_steps()
    {
        var audio = Render(Drums(Pattern("K - S -")), Rate * 2); // beats at 0, 24 000, 48 000, 72 000

        Assert.Equal(DrumSynth.Kick(Rate).AsSpan(0, 2_000).ToArray(), audio.AsSpan(0, 2_000).ToArray());
        Assert.All(audio.AsSpan(24_000, 24_000).ToArray(), v => Assert.Equal(0f, v));            // beat 2 is a rest
        Assert.Equal(DrumSynth.Snare(Rate).AsSpan(0, 2_000).ToArray(), audio.AsSpan(48_000, 2_000).ToArray());
    }

    [Fact]
    public void Hits_on_the_same_step_and_ringing_tails_are_mixed()
    {
        var both = Render(Drums(Pattern("KH -", beats: 2)), 4_000);
        var kick = DrumSynth.Kick(Rate);
        var hat = DrumSynth.HiHat(Rate);
        for (var i = 0; i < 3_000; i++)
            Assert.Equal(Math.Clamp(kick[i] + (i < hat.Length ? hat[i] : 0), -1f, 1f), both[i], 5);

        // 16th hats at 240 BPM (62.5 ms apart) over a 400 ms kick: the kick keeps ringing under every hat.
        var fast = Render(Drums(Pattern("KH H H H", beats: 1, sub: Subdivision.Sixteenths), bpm: 240), 9_000);
        var at = 3_000 + 1_000; // 1 000 samples into the 2nd hat; the 1st (3 360 samples) has ended
        Assert.Equal(Math.Clamp(kick[at] + hat[1_000], -1f, 1f), fast[at], 5);
    }

    [Fact]
    public void Click_mode_ignores_the_pattern_and_drum_mode_ignores_the_accent()
    {
        var pattern = Pattern("K S S S");
        var clicks = Render(new MetronomeSettings(Drums: pattern), 2_000);
        Assert.Equal(Render(new MetronomeSettings(), 2_000), clicks);

        var accented = Render(Drums(pattern) with { AccentFirstBeat = true }, Rate);
        var flat = Render(Drums(pattern) with { AccentFirstBeat = false }, Rate);
        Assert.Equal(accented, flat);
    }

    [Fact]
    public void Gap_training_silences_drums_too()
    {
        var settings = Drums(Pattern("H H", beats: 2)) with { GapEnabled = true, GapPlayBars = 1, GapMuteBars = 1 };
        var audio = Render(settings, 48_000 * 2); // bar 0 plays, bar 1 (48 000…96 000) silent

        Assert.True(Rms(audio.AsSpan(0, 3_000)) > 0.01);
        Assert.All(audio.AsSpan(48_000, 48_000).ToArray(), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Loud_overlapping_drums_stay_in_full_scale()
    {
        var all = Pattern("KSH KSH KSH KSH", beats: 1, sub: Subdivision.Sixteenths);
        foreach (var volume in new[] { 1f, 2f })
            Assert.All(Render(Drums(all, bpm: 400) with { Volume = volume }, Rate), v => Assert.InRange(v, -1f, 1f));
    }

    [Fact]
    public void Render_with_drums_does_not_allocate()
    {
        var engine = new MetronomeEngine(Rate, Drums(Pattern("KSH H KSH H", beats: 1, sub: Subdivision.Sixteenths), bpm: 400));
        engine.Start();
        var buffer = new float[256];
        Allocations.AssertNone(() =>
        {
            engine.Render(buffer);
            while (engine.TryDequeueTick(out _)) { }
        });
    }

    [Fact]
    public void A_pattern_on_another_grid_plays_the_steps_that_line_up()
    {
        var eighths = Pattern("K H S H", beats: 2, sub: Subdivision.Eighths);

        // Played in 16ths: steps land on 16ths 0, 2, 4, 6; odd 16ths are silent.
        Assert.Equal(
            [DrumHits.Kick, 0, DrumHits.HiHat, 0, DrumHits.Snare, 0, DrumHits.HiHat, 0],
            Enumerable.Range(0, 8).Select(i => eighths.At(i / 4, i % 4, 4)));
        // In quarters only the on-beat steps; in triplets only the downbeat lines up.
        Assert.Equal([DrumHits.Kick, DrumHits.Snare], Enumerable.Range(0, 2).Select(b => eighths.At(b, 0, 1)));
        Assert.Equal([DrumHits.Kick, 0, 0], Enumerable.Range(0, 3).Select(n => eighths.At(0, n, 3)));
        // Beats past the pattern repeat it.
        Assert.Equal(DrumHits.Kick, eighths.At(2, 0, 2));
    }

    [Fact]
    public void Resizing_keeps_what_lines_up_and_repeats_into_new_beats()
    {
        var rock = DrumPattern.BasicRock;

        var sixteenths = rock.Resize(4, Subdivision.Sixteenths);
        Assert.Equal(16, sixteenths.Steps.Length);
        Assert.Equal(rock, sixteenths.Resize(4, Subdivision.Eighths)); // round trip: nothing lost

        var five = rock.Resize(5, Subdivision.Eighths);
        Assert.Equal(rock.Steps.Concat(rock.Steps.Take(2)), five.Steps);

        Assert.Equal([DrumHits.Kick | DrumHits.HiHat, DrumHits.Snare | DrumHits.HiHat], rock.Resize(2, Subdivision.Quarters).Steps);
    }

    [Fact]
    public void Patterns_compare_by_steps_and_normalise()
    {
        var a = Pattern("K S", beats: 2);
        var b = a with { Steps = (DrumHits[])a.Steps.Clone() };
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, a.WithStep(1, DrumHits.HiHat));
        Assert.Equal(DrumHits.Snare, a.Steps[1]); // WithStep copies

        var messy = new DrumPattern("  " + new string('x', 60), 40, (Subdivision)7, [DrumHits.Kick, (DrumHits)0xFF]).Normalized();
        Assert.Equal((DrumPattern.MaxNameLength, MetronomeSettings.MaxBeatsPerBar, Subdivision.Quarters), (messy.Name.Length, messy.BeatsPerBar, messy.Subdivision));
        Assert.Equal(MetronomeSettings.MaxBeatsPerBar, messy.Steps.Length);
        Assert.Equal([DrumHits.Kick, DrumHits.All, DrumHits.None], messy.Steps.Take(3));
    }

    [Fact]
    public void Drum_settings_round_trip_through_json()
    {
        var settings = Drums(DrumPattern.BuiltIns[2]) with { Volume = 0.5f };
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        Assert.Equal(settings, System.Text.Json.JsonSerializer.Deserialize<MetronomeSettings>(json));

        // Settings saved before drums existed load as the click.
        var old = System.Text.Json.JsonSerializer.Deserialize<MetronomeSettings>("""{"Bpm":90}""")!;
        Assert.Equal((MetronomeSound.Click, (DrumPattern?)null), (old.Sound, old.Drums));
    }

    [Fact]
    public void Built_in_patterns_are_valid()
    {
        Assert.Equal(DrumPattern.BuiltIns.Count, DrumPattern.BuiltIns.Select(p => p.Name).Distinct().Count());
        Assert.All(DrumPattern.BuiltIns, p => Assert.Equal(p, p.Normalized()));
    }
}
