using GuitarPractice.Audio;

namespace GuitarPractice.Audio.Tests;

public class MetronomeEngineTests
{
    private const int Rate = 48_000;

    private static MetronomeEngine Started(MetronomeSettings settings)
    {
        var engine = new MetronomeEngine(Rate, settings);
        engine.Start();
        return engine;
    }

    private static float[] Render(MetronomeEngine engine, int samples, int chunk = 256)
    {
        var output = new float[samples];
        for (var i = 0; i < samples; i += chunk)
            engine.Render(output.AsSpan(i, Math.Min(chunk, samples - i)));
        return output;
    }

    private static List<MetronomeTick> Ticks(MetronomeEngine engine)
    {
        var ticks = new List<MetronomeTick>();
        while (engine.TryDequeueTick(out var t)) ticks.Add(t);
        return ticks;
    }

    [Fact]
    public void Clicks_land_on_exact_sample_positions()
    {
        var engine = Started(new MetronomeSettings(Bpm: 120));
        var audio = Render(engine, Rate * 2 + 1);

        var ticks = Ticks(engine);
        Assert.Equal([0L, 24_000, 48_000, 72_000, 96_000], ticks.Select(t => t.SamplePosition));

        // Sound right after each click, silence in between (the click is 15 ms long).
        foreach (var t in ticks.Take(4))
        {
            Assert.True(audio.AsSpan((int)t.SamplePosition, 200).ToArray().Max(Math.Abs) > 0.1f);
            Assert.Equal(0f, audio[(int)t.SamplePosition + 2_000]);
        }
    }

    [Fact]
    public void No_drift_when_the_period_is_not_a_whole_number_of_samples()
    {
        // 137 BPM at 48 kHz = 21021.897... samples per beat.
        var engine = Started(new MetronomeSettings(Bpm: 137));
        var period = Rate * 60.0 / 137;
        var ticks = new List<MetronomeTick>();
        var buffer = new float[512];
        while (ticks.Count < 1_000)
        {
            engine.Render(buffer);
            while (engine.TryDequeueTick(out var t)) ticks.Add(t);
        }

        for (var k = 0; k < 1_000; k++)
            Assert.Equal((long)Math.Ceiling(k * period - 1e-6), ticks[k].SamplePosition);
    }

    [Fact]
    public void Output_does_not_depend_on_buffer_size()
    {
        var settings = new MetronomeSettings(Bpm: 173, Subdivision: Subdivision.Triplets);
        var reference = Render(Started(settings), Rate * 3, chunk: Rate * 3);

        foreach (var chunk in new[] { 1, 7, 64, 333, 4096 })
            Assert.Equal(reference, Render(Started(settings), Rate * 3, chunk));
    }

    [Fact]
    public void Bars_beats_and_accents_cycle()
    {
        var engine = Started(new MetronomeSettings(Bpm: 240, BeatsPerBar: 3, AccentFirstBeat: true));
        Render(engine, Rate * 2); // 8 beats

        var ticks = Ticks(engine);
        Assert.Equal([0, 1, 2, 0, 1, 2, 0, 1], ticks.Select(t => t.Beat));
        Assert.Equal([0L, 0, 0, 1, 1, 1, 2, 2], ticks.Select(t => t.Bar));
        Assert.Equal(
            [ClickKind.Accent, ClickKind.Beat, ClickKind.Beat, ClickKind.Accent, ClickKind.Beat, ClickKind.Beat, ClickKind.Accent, ClickKind.Beat],
            ticks.Select(t => t.Kind));
    }

    [Fact]
    public void Accent_off_makes_every_beat_the_same()
    {
        var engine = Started(new MetronomeSettings(Bpm: 240, AccentFirstBeat: false));
        Render(engine, Rate * 2);
        Assert.All(Ticks(engine), t => Assert.Equal(ClickKind.Beat, t.Kind));
    }

    [Fact]
    public void Subdivisions_split_the_beat_evenly()
    {
        var engine = Started(new MetronomeSettings(Bpm: 60, Subdivision: Subdivision.Sixteenths));
        Render(engine, Rate + 1);

        var ticks = Ticks(engine);
        Assert.Equal([0L, 12_000, 24_000, 36_000, 48_000], ticks.Select(t => t.SamplePosition));
        Assert.Equal([0, 1, 2, 3, 0], ticks.Select(t => t.SubTick));
        Assert.Equal(
            [ClickKind.Accent, ClickKind.Subdivision, ClickKind.Subdivision, ClickKind.Subdivision, ClickKind.Beat],
            ticks.Select(t => t.Kind));
    }

    [Fact]
    public void Tempo_change_takes_effect_at_the_next_click()
    {
        var engine = Started(new MetronomeSettings(Bpm: 120));
        Render(engine, 10_000);                          // click at 0; next already scheduled for 24 000
        engine.Settings = new MetronomeSettings(Bpm: 60);
        Render(engine, Rate * 2);

        Assert.Equal([0L, 24_000, 72_000], Ticks(engine).Select(t => t.SamplePosition));
    }

    [Fact]
    public void Shrinking_the_bar_mid_bar_wraps_to_beat_one()
    {
        var engine = Started(new MetronomeSettings(Bpm: 240, BeatsPerBar: 7));
        Render(engine, 12_000 * 5 + 1);                   // beats 0..5 played
        engine.Settings = new MetronomeSettings(Bpm: 240, BeatsPerBar: 3);
        Render(engine, 12_000 * 3);

        Assert.Equal([0, 1, 2, 3, 4, 5, 0, 1, 2], Ticks(engine).Select(t => t.Beat));
    }

    [Fact]
    public void Silent_until_started_and_after_stop_and_restart_begins_on_beat_one()
    {
        var engine = new MetronomeEngine(Rate, new MetronomeSettings(Bpm: 120));
        Assert.All(Render(engine, 4_096), v => Assert.Equal(0f, v));

        engine.Start();
        Render(engine, 30_000);
        engine.Stop();
        Assert.All(Render(engine, 4_096), v => Assert.Equal(0f, v));

        Ticks(engine);
        engine.Start();
        Render(engine, 256);
        var restart = Assert.Single(Ticks(engine));
        Assert.Equal((0L, 0, ClickKind.Accent), (restart.SamplePosition, restart.Beat, restart.Kind));
    }

    [Fact]
    public void Volume_scales_and_output_stays_in_range()
    {
        var loud = Render(Started(new MetronomeSettings(Volume: 1f)), 2_000);
        var quiet = Render(Started(new MetronomeSettings(Volume: 0.25f)), 2_000);

        Assert.InRange(loud.Max(Math.Abs), 0.5f, 1f);
        Assert.Equal(loud.Max(Math.Abs) * 0.25f, quiet.Max(Math.Abs), 4);
    }

    private static double Db(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (var v in x) sum += v * v;
        return 10 * Math.Log10(sum / x.Length);
    }

    [Fact]
    public void Clicks_carry_much_more_energy_than_the_original_design()
    {
        // The original click: 15 ms pure sine, 4 ms decay, beat peak 0.75 at the old default volume 0.8.
        var old = new float[(int)(Rate * 0.035)];
        for (var i = 0; i < (int)(Rate * 0.015); i++)
            old[i] = (float)(0.8 * 0.75 * Math.Exp(-i / (double)Rate / 0.004) * Math.Sin(2 * Math.PI * 1000 * i / Rate));

        var engine = Started(new MetronomeSettings(Bpm: 60, AccentFirstBeat: false)); // default volume, beat click
        var now = Render(engine, old.Length);

        var gain = Db(now) - Db(old);
        Assert.True(gain > 7, $"only {gain:0.0} dB louder"); // measured +7.6 dB
    }

    [Fact]
    public void Volume_above_100_percent_is_louder_without_exceeding_full_scale()
    {
        var normal = Render(Started(new MetronomeSettings(Volume: 1f)), 2_000);
        var boosted = Render(Started(new MetronomeSettings(Volume: 2f)), 2_000);

        Assert.True(boosted.Max(Math.Abs) <= 1f);
        Assert.True(Db(boosted) - Db(normal) > 3, // measured +3.9 dB
            $"boost added only {Db(boosted) - Db(normal):0.0} dB");
    }

    [Fact]
    public void Settings_are_clamped()
    {
        var s = new MetronomeSettings(Bpm: 5000, BeatsPerBar: 0, Volume: 3f, Subdivision: (Subdivision)9).Normalized();
        Assert.Equal((MetronomeSettings.MaxBpm, 1, MetronomeSettings.MaxVolume, Subdivision.Quarters), (s.Bpm, s.BeatsPerBar, s.Volume, s.Subdivision));
        Assert.Equal(MetronomeSettings.MinBpm, new MetronomeSettings(Bpm: 1).Normalized().Bpm);
        Assert.Equal(120, new MetronomeSettings(Bpm: double.NaN).Normalized().Bpm);
    }

    // ---- Gap training ----

    [Fact]
    public void Gap_training_mutes_whole_bars_and_keeps_time()
    {
        // 240 BPM, 2/4: one bar = 24 000 samples. Play 2, mute 1.
        var engine = Started(new MetronomeSettings(Bpm: 240, BeatsPerBar: 2, GapEnabled: true, GapPlayBars: 2, GapMuteBars: 1));
        var audio = Render(engine, 24_000 * 6 + 1);

        var ticks = Ticks(engine);
        Assert.Equal(Enumerable.Range(0, 13).Select(k => k * 12_000L), ticks.Select(t => t.SamplePosition)); // timing unchanged
        Assert.Equal(
            [false, false, true, false, false, true, false],
            ticks.Where(t => t.Beat == 0).Select(t => t.Muted));

        float Peak(int bar) => audio.AsSpan(bar * 24_000, 24_000).ToArray().Max(Math.Abs);
        Assert.True(Peak(0) > 0.1f && Peak(1) > 0.1f && Peak(3) > 0.1f && Peak(4) > 0.1f);
        Assert.Equal(0f, Peak(2));
        Assert.Equal(0f, Peak(5));
    }

    [Fact]
    public void Gap_training_switched_on_mid_bar_starts_counting_at_the_next_bar()
    {
        var engine = Started(new MetronomeSettings(Bpm: 240, BeatsPerBar: 2));
        Render(engine, 12_000 + 1); // bar 0, both beats
        engine.Settings = new MetronomeSettings(Bpm: 240, BeatsPerBar: 2, GapEnabled: true, GapPlayBars: 1, GapMuteBars: 1);
        Render(engine, 24_000 * 3);

        var downbeats = Ticks(engine).Where(t => t.Beat == 0).ToList();
        Assert.Equal([0L, 1, 2, 3], downbeats.Select(t => t.Bar));
        Assert.Equal([false, false, true, false], downbeats.Select(t => t.Muted)); // bar 1 = play, bar 2 = mute
    }

    // ---- Tempo trainer ----

    [Fact]
    public void Trainer_steps_up_on_downbeats_and_stops_at_the_target()
    {
        // 1 beat per bar for simple arithmetic; +30 BPM every 2 bars from 60 up to 120.
        var engine = Started(new MetronomeSettings(
            Bpm: 60, BeatsPerBar: 1, TrainerEnabled: true, TrainerStepBpm: 15, TrainerEveryBars: 2, TrainerTargetBpm: 90));
        Render(engine, Rate * 8);

        var ticks = Ticks(engine);
        Assert.Equal([60.0, 60, 75, 75, 90, 90, 90, 90], ticks.Take(8).Select(t => t.Bpm));

        // Each bar is played at its own tempo: 48000, 48000, 38400, 38400, 32000, ...
        long[] expected = [0, 48_000, 96_000, 134_400, 172_800, 204_800, 236_800, 268_800];
        Assert.Equal(expected, ticks.Take(8).Select(t => t.SamplePosition));
    }

    [Fact]
    public void Trainer_never_lowers_the_tempo_when_the_target_is_below_it()
    {
        var engine = Started(new MetronomeSettings(
            Bpm: 120, BeatsPerBar: 1, TrainerEnabled: true, TrainerStepBpm: 10, TrainerEveryBars: 1, TrainerTargetBpm: 100));
        Render(engine, Rate * 3);
        Assert.All(Ticks(engine), t => Assert.Equal(120, t.Bpm));
    }

    [Fact]
    public void Changing_the_base_tempo_restarts_the_ramp()
    {
        var engine = Started(new MetronomeSettings(
            Bpm: 120, BeatsPerBar: 1, TrainerEnabled: true, TrainerStepBpm: 10, TrainerEveryBars: 1, TrainerTargetBpm: 200));
        Render(engine, 24_000 * 3 + 1); // bars at 120, 130, 140, 150 (sample 0, 24000, 46154, 66726)
        Assert.Equal([120.0, 130, 140, 150], Ticks(engine).Select(t => t.Bpm));

        engine.Settings = new MetronomeSettings(
            Bpm: 100, BeatsPerBar: 1, TrainerEnabled: true, TrainerStepBpm: 10, TrainerEveryBars: 1, TrainerTargetBpm: 200);
        Render(engine, Rate * 3);
        Assert.Equal([100.0, 110, 120], Ticks(engine).Take(3).Select(t => t.Bpm));
    }

    [Fact]
    public void Settings_round_trip_through_json_for_remembering_between_sessions()
    {
        var settings = new MetronomeSettings(
            Bpm: 133, BeatsPerBar: 7, Subdivision: Subdivision.Triplets, AccentFirstBeat: false, Volume: 0.35f,
            GapEnabled: true, GapPlayBars: 2, GapMuteBars: 3,
            TrainerEnabled: true, TrainerStepBpm: 5, TrainerEveryBars: 8, TrainerTargetBpm: 180);

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        Assert.Equal(settings, System.Text.Json.JsonSerializer.Deserialize<MetronomeSettings>(json));

        // Settings saved before gap/trainer existed still load, with defaults for the new fields.
        var old = System.Text.Json.JsonSerializer.Deserialize<MetronomeSettings>("""{"Bpm":90,"BeatsPerBar":3}""")!;
        Assert.Equal(new MetronomeSettings(Bpm: 90, BeatsPerBar: 3), old);
    }

    [Fact]
    public void Render_does_not_allocate()
    {
        var engine = Started(new MetronomeSettings(
            Bpm: 300, Subdivision: Subdivision.Sixteenths,
            GapEnabled: true, GapPlayBars: 1, GapMuteBars: 1, TrainerEnabled: true, TrainerEveryBars: 1, TrainerTargetBpm: 400));
        var buffer = new float[256];
        Allocations.AssertNone(() =>
        {
            engine.Render(buffer);
            while (engine.TryDequeueTick(out _)) { }
        });
    }

    [Fact]
    public void Tick_queue_drops_instead_of_blocking_when_the_ui_falls_behind()
    {
        var engine = Started(new MetronomeSettings(Bpm: 400, Subdivision: Subdivision.Sixteenths));
        Render(engine, Rate * 20); // ~530 clicks, nobody reading

        Assert.Equal(256, Ticks(engine).Count);
    }

    [Theory]
    [InlineData(Subdivision.Quintuplets, 5)]
    [InlineData(Subdivision.Sextuplets, 6)]
    [InlineData(Subdivision.Septuplets, 7)]
    [InlineData(Subdivision.ThirtySeconds, 8)]
    public void Odd_groupings_and_32nds_split_the_beat_evenly(Subdivision subdivision, int perBeat)
    {
        var engine = Started(new MetronomeSettings(Bpm: 120, BeatsPerBar: 2, Subdivision: subdivision));
        Render(engine, Rate + 1); // one bar of two beats at 120 = 48 000 samples

        var ticks = Ticks(engine);
        // Each click on the first sample at or after its exact time (a 7th of a beat is 3428.57 samples here).
        Assert.Equal(Enumerable.Range(0, 2 * perBeat + 1).Select(i => (long)Math.Ceiling(i * 24_000.0 / perBeat - 1e-6)), ticks.Select(t => t.SamplePosition));
        Assert.Equal(3, ticks.Count(t => t.Kind != ClickKind.Subdivision)); // beats 1 and 2, then the next bar's 1
        Assert.Equal(perBeat - 1, ticks.Take(perBeat).Count(t => t.Kind == ClickKind.Subdivision));
        Assert.All(Render(Started(new MetronomeSettings(Bpm: 400, Subdivision: subdivision, Volume: 2f)), Rate), v => Assert.InRange(v, -1f, 1f));
    }
}
