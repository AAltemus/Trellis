namespace GuitarPractice.Audio.Tests;

public class RhythmTrainerTests
{
    private const int Rate = 48_000;
    private const int Q = 480;                  // ticks per quarter
    private const double SecondsPerTick = 0.5 / Q; // 120 BPM

    private static RhythmTrainerPlan FourFour(int[] onsets, int[] lengths, bool listen = true, int bars = 2) =>
        new(SecondsPerTick, 4 * Q, [0, Q, 2 * Q, 3 * Q], onsets, lengths, listen, bars);

    /// <summary>Runs the take to the end in 256-sample blocks; <paramref name="input"/>(stream sample) feeds the input.</summary>
    private static float[] Run(RhythmTrainerEngine engine, Func<long, float>? input = null)
    {
        engine.Start();
        var output = new List<float>();
        var inBuf = new float[256];
        var outBuf = new float[256];
        long s = 0;
        engine.Process(inBuf, outBuf); // applies Start (and renders the first block)
        output.AddRange(outBuf);
        s += 256;
        while (engine.State != RhythmTrainerState.Done)
        {
            for (var i = 0; i < 256; i++) inBuf[i] = input?.Invoke(s + i) ?? 0f;
            engine.Process(inBuf, outBuf);
            output.AddRange(outBuf);
            s += 256;
            Assert.True(s < 60 * Rate, "the take never finished");
        }
        return [.. output];
    }

    /// <summary>Starts of sounds in <paramref name="output"/>: a sample above 0.02 after 0.1 s below it.</summary>
    private static List<double> SoundStarts(float[] output, int from = 0, int to = int.MaxValue)
    {
        var starts = new List<double>();
        var quiet = Rate;
        for (var i = from; i < Math.Min(to, output.Length); i++)
        {
            if (Math.Abs(output[i]) < 0.02f) { quiet = Math.Min(quiet + 1, Rate); continue; }
            if (quiet > Rate / 10) starts.Add((double)i / Rate);
            quiet = 0;
        }
        return starts;
    }

    [Fact]
    public void A_take_is_count_in_then_listen_then_your_bars()
    {
        var engine = new RhythmTrainerEngine(Rate, FourFour([0, Q, 2 * Q, 3 * Q], [Q, Q, Q, Q], listen: true, bars: 2));
        Assert.Equal(engine.At(2, 0), engine.PlayStart);                 // count-in + listen bar = 4 s
        Assert.Equal(4 * Rate, engine.PlayStart);
        Assert.Equal(8 * Rate, engine.PlayEnd);

        engine.ClickEnabled = false;
        engine.DrumsEnabled = false;
        var output = Run(engine);

        // Count-in clicks (always on) every 0.5 s; then only the rhythm in the listen bar; your bars are silent.
        Assert.Equal([0.0, 0.5, 1.0, 1.5], SoundStarts(output, 0, 2 * Rate).Select(t => Math.Round(t, 3)));
        Assert.Equal(2.0, Math.Round(SoundStarts(output, 2 * Rate, 4 * Rate)[0], 3));
        Assert.Empty(SoundStarts(output, (int)(4.6 * Rate), 8 * Rate));
    }

    [Fact]
    public void Without_listening_your_bars_start_right_after_the_count_in()
    {
        var engine = new RhythmTrainerEngine(Rate, FourFour([0], [4 * Q], listen: false, bars: 1));
        Assert.Equal(2 * Rate, engine.PlayStart);
        Assert.Equal(0, engine.ListenBars);
    }

    [Fact]
    public void Click_and_drums_sound_in_the_bars_you_play()
    {
        float[] Take(bool click, bool drums)
        {
            var engine = new RhythmTrainerEngine(Rate, FourFour([0], [4 * Q], listen: false, bars: 1)) { ClickEnabled = click, DrumsEnabled = drums };
            return Run(engine);
        }
        var silent = Take(click: false, drums: false);
        Assert.Empty(SoundStarts(silent, 2 * Rate, 4 * Rate));               // only the count-in clicks

        // Drums alone: an attack on every 8th (hats; kick and snare on the beats).
        var drums = Take(click: false, drums: true);
        var hits = OnsetDetector.Detect(drums.AsSpan(2 * Rate - Rate / 20, 2 * Rate), Rate); // from 50 ms before the bar
        Assert.Equal([0.0, 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75], hits.Select(t => Math.Round(t - 0.05, 2)));

        // The click alone: on the beats.
        var click = Take(click: true, drums: false);
        Assert.Equal([0.0, 0.5, 1.0, 1.5], SoundStarts(click, 2 * Rate, 4 * Rate).Select(t => Math.Round(t - 2, 3)));
    }

    [Fact]
    public void Groove_puts_kick_and_snare_on_the_beats_and_hats_on_8ths()
    {
        var fourFour = RhythmTrainerEngine.Groove(FourFour([0], [4 * Q]));
        Assert.Equal(8, fourFour.Count);
        Assert.Equal(DrumHits.Kick | DrumHits.HiHat, fourFour[0].Hits);
        Assert.Equal(DrumHits.Snare | DrumHits.HiHat, fourFour.Single(h => h.Tick == Q).Hits);
        Assert.Equal(DrumHits.Kick | DrumHits.HiHat, fourFour.Single(h => h.Tick == 2 * Q).Hits);
        Assert.Equal(DrumHits.HiHat, fourFour.Single(h => h.Tick == Q / 2).Hits);

        // 7/8 as 2+2+3: K S K on the group starts.
        var sevenEight = RhythmTrainerEngine.Groove(new RhythmTrainerPlan(SecondsPerTick, 7 * Q / 2, [0, Q, 2 * Q], [0], [Q]));
        Assert.Equal(7, sevenEight.Count);
        Assert.Equal([DrumHits.Kick, DrumHits.Snare, DrumHits.Kick],
            new[] { 0, Q, 2 * Q }.Select(t => sevenEight.Single(h => h.Tick == t).Hits & ~DrumHits.HiHat));
    }

    [Fact]
    public void What_you_play_is_recorded_where_you_heard_it()
    {
        // You play 8th notes for two bars; your notes reach the input 7 ms (the round trip) after the beat you heard.
        int[] onsets = [0, Q / 2, Q, 3 * Q / 2, 2 * Q, 5 * Q / 2, 3 * Q, 7 * Q / 2];
        var engine = new RhythmTrainerEngine(Rate, FourFour(onsets, [.. onsets.Select(_ => Q / 2)], listen: false, bars: 2));
        var latency = (int)(0.007 * Rate);
        engine.LatencySamples = latency;
        var pluck = PluckSynth.Render(Rate, [new SynthNote(0, 40, 0.2), new SynthNote(0, 47, 0.2), new SynthNote(0, 52, 0.2)]);
        var played = new SortedSet<long>();
        for (var bar = 0; bar < 2; bar++)
            foreach (var t in onsets) played.Add(engine.PlayStart + engine.At(bar, t) - engine.At(0, 0) + latency);
        float Input(long s)
        {
            var value = 0f;
            foreach (var p in played.GetViewBetween(s - pluck.Length + 1, s)) value += pluck[s - p];
            return value;
        }

        Run(engine, Input);
        var preRoll = engine.PreRollSamples / (double)Rate;
        Assert.Equal(0.1, preRoll, 6);
        var detected = OnsetDetector.Detect(engine.Recording, Rate).Select(t => t - preRoll).ToList();
        var expected = Enumerable.Range(0, 2).SelectMany(bar => onsets.Select(t => (bar * 4 * Q + t) * SecondsPerTick)).ToList();
        Assert.Equal(expected.Count, detected.Count);
        for (var i = 0; i < expected.Count; i++) Assert.InRange(detected[i] - expected[i], -0.002, 0.002);
    }

    [Fact]
    public void Onsets_are_found_in_ringing_chords_quick_notes_and_noise()
    {
        // Ringing E5 chords: quarters, then 16ths at 120 BPM (125 ms apart) over the previous ring, then a gap.
        double[] times = [0.10, 0.60, 1.10, 1.60, 1.725, 1.85, 1.975, 2.60];
        var notes = times.SelectMany(t => new[] { 40, 47, 52 }.Select(m => new SynthNote(t, m, 0.45))).ToList();
        var signal = PluckSynth.Render(Rate, notes);
        var noise = new Random(7);
        for (var i = 0; i < signal.Length; i++) signal[i] += (float)((noise.NextDouble() * 2 - 1) * 0.003); // ≈ −50 dBFS

        var detected = OnsetDetector.Detect(signal, Rate);
        Assert.Equal(times.Length, detected.Count);
        for (var i = 0; i < times.Length; i++) Assert.InRange(detected[i] - times[i], -0.002, 0.003);
    }

    /// <summary>
    /// Like a guitar DI: a low E chord rich in harmonics (sawtooth-ish to 5 kHz), two strings a little out of tune with
    /// each other (beating), ringing a whole quarter note at 80 BPM before the next strum. Within one 12 ms period of
    /// E2 the attack-sensitive envelope swings a lot; that must not read as new notes (user, 2026-10-26: 16/16 hit,
    /// 68 extra on quarter notes).
    /// </summary>
    [Fact]
    public void A_ringing_harmonic_rich_chord_is_one_onset_per_strum()
    {
        double[] times = [0.10, 0.85, 1.60, 2.35, 3.10];
        var signal = new float[(int)(3.9 * Rate)];
        foreach (var t in times)
        {
            var start = (int)(t * Rate);
            for (var i = 0; i < 0.75 * Rate && start + i < signal.Length; i++)
            {
                var s = (double)i / Rate;
                var v = 0.0;
                foreach (var (hz, gain) in new[] { (82.41, 1.0), (123.47, 0.8), (164.81 * 1.004, 0.7) }) // E2, B2, E3 slightly sharp
                    for (var h = 1; h * hz < 5000; h++)
                        v += gain / h * Math.Sin(2 * Math.PI * hz * h * s) * Math.Exp(-s * (1.5 + h * 0.4));
                var attack = Math.Min(1, s / 0.002) * Math.Min(1, (0.75 - s) / 0.005); // 2 ms in, 5 ms out (no step at the end)
                signal[start + i] += (float)(0.12 * attack * v);
            }
        }

        var detected = OnsetDetector.Detect(signal, Rate);
        Assert.True(times.Length == detected.Count, "detected: " + string.Join(", ", detected.Select(d => d.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))));
        // This chord has no pick transient (its steepest slope comes a few ms into the first cycle), so it reads up to
        // ~6 ms after its start; timing precision is covered by the plucked-string tests above.
        for (var i = 0; i < times.Length; i++) Assert.InRange(detected[i] - times[i], -0.002, 0.008);
    }

    /// <summary>
    /// Shaped like the user's real take (2026-10-26, saved WAV): low E 8ths at 80 BPM, the string damped ~13 dB in the
    /// 40 ms before each next note, soft attacks with almost no pick click, upstrokes weaker and darker. The click cue
    /// alone found 13 of 32 such notes; the level cue finds them all.
    /// </summary>
    [Fact]
    public void Muted_8ths_with_soft_attacks_are_all_found()
    {
        const double Eighth = 0.375;
        var signal = new float[(int)(16 * Eighth * Rate + Rate / 2)];
        for (var n = 0; n < 16; n++)
        {
            var start = (int)((0.1 + n * Eighth) * Rate);
            var up = n % 2 == 1;
            for (var i = 0; i < Eighth * Rate && start + i < signal.Length; i++)
            {
                var s = (double)i / Rate;
                var v = 0.0;
                for (var h = 1; h * 82.41 < (up ? 1500 : 3000); h++)
                    v += 1.0 / h * Math.Sin(2 * Math.PI * 82.41 * h * s) * Math.Exp(-s * (1 + h * 0.3));
                var attack = Math.Min(1, s / 0.004);                                   // soft: 4 ms, no click
                var mute = s < Eighth - 0.04 ? 1 : Math.Pow(10, -13 / 20.0 * (s - (Eighth - 0.04)) / 0.04); // damped before the next
                signal[start + i] = (float)((up ? 0.7 : 1.0) * 0.05 * attack * mute * v);
            }
        }

        var detected = OnsetDetector.Detect(signal, Rate);
        Assert.True(detected.Count == 16, "detected: " + string.Join(", ", detected.Select(d => d.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))));
        for (var n = 0; n < 16; n++) Assert.InRange(detected[n] - (0.1 + n * Eighth), -0.003, 0.006);
    }

    /// <summary>A low E power chord (E2 B2 E3; the E3 a little sharp, so it beats) rich in harmonics, from 0 for <paramref name="seconds"/>.</summary>
    private static double Chord(double s) =>
        new[] { (82.41, 1.0), (123.47, 0.8), (164.81 * 1.004, 0.7) }
            .Sum(p => Enumerable.Range(1, (int)(3000 / p.Item1)).Sum(h => p.Item2 / h * Math.Sin(2 * Math.PI * p.Item1 * h * s)));

    /// <summary>
    /// The user's gallop take (2026-10-26): the last 16th of e-s-s comes right after the middle one with no mute between,
    /// so the chord gets only ~3 dB louder and the click barely shows. Not found on its own; found where it's due.
    /// </summary>
    [Fact]
    public void A_quiet_re_strike_on_a_ringing_chord_is_found_where_a_note_is_due()
    {
        // Gallop at 80: 8th at 0, 16ths at 0.375 and 0.5625 s; muted before each next beat (0.75 s).
        double[] times = [0.10, 0.475, 0.6625];
        var signal = new float[(int)(0.95 * Rate)];
        for (var i = 0; i < signal.Length; i++)
        {
            var t = (double)i / Rate;
            if (t < times[0]) continue;
            var since = t - times.Last(x => x <= t);
            var gain = t >= times[2] ? 1.41 : t >= times[1] ? 1.0 : 1.0;                // the last 16th: +3 dB
            var attack = Math.Min(1, since / 0.004);
            var level = t < times[1] ? Math.Exp(-(t - times[0]) * 2) * (t > times[1] - 0.04 ? 0.22 : 1) : Math.Exp(-(t - times[1]) * 2);
            signal[i] = (float)(0.03 * gain * level * (t >= times[2] ? Math.Max(attack, 1 / 1.41) : t >= times[1] ? attack : attack) * Chord(t - times[0]));
        }
        // The re-strike's pick: a 2 ms bright tick, well below the chord (a real pick always makes one).
        var noise = new Random(5);
        var pick = (int)(times[2] * Rate);
        for (var i = 0; i < Rate / 500; i++) signal[pick + i] += (float)((noise.NextDouble() * 2 - 1) * 0.012 * (1 - i / (Rate / 500.0)));

        var clear = OnsetDetector.Detect(signal, Rate);
        Assert.Equal(2, clear.Count);                                                     // the 8th and the first 16th
        var near = OnsetDetector.DetectNear(signal, Rate, times, 0.08);
        Assert.True(near.Count == 3, "near: " + string.Join(", ", near.Select(d => d.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))));
        Assert.InRange(near[2] - times[2], -0.010, 0.010);
    }

    /// <summary>
    /// The user's 16ths at 140 (2026-10-26): an upstroke on a ringing low E added no level and no brightness, but the
    /// string's vibration restarted (cycle-to-cycle similarity 0.9 → 0.3). Same level, same spectrum, new shape.
    /// </summary>
    [Fact]
    public void A_re_pick_that_only_changes_the_waveform_shape_is_found_where_due()
    {
        double[] times = [0.10, 0.207];              // a downstroke, then the next 16th at 140 BPM
        var signal = new float[(int)(0.5 * Rate)];
        static double Wave(double s, double phase)
        {
            var v = 0.0;
            for (var h = 1; h * 82.41 < 2500; h++) v += 1.0 / h * Math.Sin(2 * Math.PI * 82.41 * h * s + h * phase);
            return v;
        }
        for (var i = (int)(times[0] * Rate); i < signal.Length; i++)
        {
            var t = (double)i / Rate;
            var s = t - times[0];
            // The re-pick: same harmonics and level, new phases, blended in over 3 ms (a pick is no step).
            var mix = Math.Clamp((t - times[1]) / 0.003, 0, 1);
            var v = (1 - mix) * Wave(s, 0) + mix * Wave(t - times[1], 2.1);
            signal[i] = (float)(0.05 * Math.Min(1, s / 0.004) * v * Math.Exp(-s * 2));
        }

        Assert.Single(OnsetDetector.Detect(signal, Rate));                                // only the downstroke is clear
        var near = OnsetDetector.DetectNear(signal, Rate, times, 0.048);
        Assert.True(near.Count == 2, "near: " + string.Join(", ", near.Select(d => d.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))));
        Assert.InRange(near[1] - times[1], -0.006, 0.006);
    }

    [Fact]
    public void Nothing_is_credited_where_a_chord_only_rings()
    {
        // One strum, then the chord rings and beats for 2 s while 16ths are "due" every 0.1875 s.
        var signal = new float[(int)(2.3 * Rate)];
        for (var i = (int)(0.1 * Rate); i < signal.Length; i++)
        {
            var s = i / (double)Rate - 0.1;
            signal[i] = (float)(0.03 * Math.Min(1, s / 0.004) * Math.Exp(-s * 1.2) * Chord(s));
        }
        var due = Enumerable.Range(0, 12).Select(k => 0.1 + k * 0.1875).ToList();
        var near = OnsetDetector.DetectNear(signal, Rate, due, 0.08);
        Assert.True(near.Count == 1, "near: " + string.Join(", ", near.Select(d => d.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact]
    public void Silence_and_steady_noise_have_no_onsets()
    {
        Assert.Empty(OnsetDetector.Detect(new float[Rate], Rate));
        var noise = new Random(3);
        var hiss = new float[Rate];
        for (var i = 0; i < hiss.Length; i++) hiss[i] = (float)((noise.NextDouble() * 2 - 1) * 0.01);
        Assert.True(OnsetDetector.Detect(hiss, Rate).Count <= 1);
    }

    [Fact]
    public void Process_does_not_allocate()
    {
        var engine = new RhythmTrainerEngine(Rate, FourFour([0, Q, 2 * Q, 3 * Q], [Q, Q, Q, Q], listen: true, bars: 4));
        engine.Start();
        var input = new float[256];
        var output = new float[256];
        Allocations.AssertNone(() => engine.Process(input, output));
    }
}
