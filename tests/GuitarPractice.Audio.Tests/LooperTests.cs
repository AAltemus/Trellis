namespace GuitarPractice.Audio.Tests;

public class LooperTests
{
    private const int Rate = 48_000;
    private const int Block = 256;
    private static readonly Dictionary<long, float> NoNotes = [];

    /// <summary>
    /// Runs the looper like the duplex stream: the "player" plays <paramref name="notes"/> (absolute heard-time sample →
    /// amplitude, 20 samples long each) and the interface delivers them <paramref name="roundTrip"/> samples later.
    /// Returns everything the looper played.
    /// </summary>
    private static float[] Run(Looper looper, double seconds, int roundTrip, IReadOnlyDictionary<long, float> notes, Action<long>? atSample = null)
    {
        var total = (int)(seconds * Rate);
        var played = new float[total];
        var input = new float[Block];
        for (long s = 0; s < total; s += Block)
        {
            atSample?.Invoke(s);
            for (var i = 0; i < Block; i++)
            {
                var heard = s + i - roundTrip;
                input[i] = 0;
                foreach (var (at, amp) in notes)
                    if (heard >= at && heard < at + 20) input[i] = amp;
            }
            var n = (int)Math.Min(Block, total - s);
            looper.Process(input.AsSpan(0, n), played.AsSpan((int)s, n));
        }
        return played;
    }

    private static long FirstAbove(float[] x, long from, float level)
    {
        for (var i = from; i < x.Length; i++)
            if (Math.Abs(x[i]) > level) return i;
        return -1;
    }

    [Fact]
    public void Loop_length_is_whole_beats_after_a_bar_of_count_in()
    {
        var looper = new Looper(Rate, 120, 4, 2);
        Assert.Equal(Rate * 4, looper.LoopLength);           // 8 beats of 0.5 s
        Assert.Equal(Rate * 2, looper.CountInLength);         // one bar
        var odd = new Looper(44_100, 97, 3, 1);
        Assert.Equal((int)(Math.Round(60.0 / 97 * 44_100 * 1000) * 3 / 1000), odd.LoopLength);
    }

    [Fact]
    public void A_note_played_on_a_heard_position_lands_there_with_latency_compensation()
    {
        const int roundTrip = 480; // 10 ms
        foreach (var (compensate, expectedShift) in new[] { (true, 0), (false, roundTrip) })
        {
            var looper = new Looper(Rate, 120, 4, 1) { LatencySamples = compensate ? roundTrip : 0, ClickEnabled = false };
            var countIn = looper.CountInLength;
            var loop = looper.LoopLength;
            looper.Start();
            Assert.True(looper.ArmRecord());
            var noteAt = countIn + 10_000;                    // heard 10 000 samples into the first pass
            var played = Run(looper, (countIn + 3 * loop) / (double)Rate, roundTrip, new Dictionary<long, float> { [noteAt] = 0.5f });

            Assert.Equal(1, looper.LayerCount);
            var second = countIn + loop;                     // the layer plays back from the second pass
            Assert.Equal(second + 10_000 + expectedShift, FirstAbove(played, second, 0.4f));
        }
    }

    [Fact]
    public void Back_to_back_overdubs_record_every_pass_and_undo_and_clear_remove_layers()
    {
        const int roundTrip = 960;
        var looper = new Looper(Rate, 120, 4, 1) { LatencySamples = roundTrip, ClickEnabled = false };
        var countIn = looper.CountInLength;
        var loop = looper.LoopLength;
        looper.Start();
        looper.ArmRecord();
        var armedAgain = false;
        var notes = new Dictionary<long, float> { [countIn + 5_000] = 0.3f, [countIn + loop + 20_000] = 0.3f };
        var played = Run(looper, (countIn + 4 * loop) / (double)Rate, roundTrip, notes, s =>
        {
            if (!armedAgain && looper.IsRecording) armedAgain = looper.ArmRecord(); // arm the next pass straight away
        });

        Assert.True(armedAgain);
        Assert.Equal(2, looper.LayerCount);                   // passes 1 and 2, none skipped
        var third = countIn + 2 * loop;
        Assert.Equal(third + 5_000, FirstAbove(played, third, 0.25f));
        Assert.Equal(third + 20_000, FirstAbove(played, third + 10_000, 0.25f));
        var mix = looper.MixDown()!;
        Assert.Equal(loop, mix.Length);
        Assert.InRange(mix[5_005], 0.29f, 0.31f);

        looper.Undo();
        Run(looper, 0.01, 0, new Dictionary<long, float>());
        Assert.Equal(1, looper.LayerCount);
        looper.Clear();
        Run(looper, 0.01, 0, new Dictionary<long, float>());
        Assert.Equal(0, looper.LayerCount);
        Assert.Null(looper.MixDown());
    }

    [Fact]
    public void Clicks_fall_on_every_beat_and_stop_cuts_an_unfinished_pass()
    {
        var looper = new Looper(Rate, 120, 4, 1);
        looper.Start();
        var played = Run(looper, 4.1, 0, new Dictionary<long, float>());
        for (var beat = 0; beat < 8; beat++)
            Assert.InRange(FirstAbove(played, beat * Rate / 2, 0.05f), beat * Rate / 2, beat * Rate / 2 + 48);
        Assert.True(looper.IsRunning && !looper.IsCountingIn);

        looper.ArmRecord();
        Run(looper, 2.5, 0, new Dictionary<long, float>());
        looper.Stop();
        Run(looper, 0.01, 0, new Dictionary<long, float>());
        Assert.False(looper.IsRunning);
        Assert.Equal(0, looper.LayerCount);                   // the pass in progress isn't kept
    }

    [Fact]
    public void A_clear_or_stop_still_pending_doesnt_cancel_a_layer_armed_after_it()
    {
        var looper = new Looper(Rate, 120, 4, 1) { ClickEnabled = false };
        looper.Start();
        looper.ArmRecord();
        Run(looper, 4.5, 0, new Dictionary<long, float> { [Rate * 2 + 1_000] = 0.3f });
        Assert.Equal(1, looper.LayerCount);

        // Nothing is processing (stream stopped): Clear and Stop wait; then Start + arm a new layer.
        looper.Clear();
        looper.Stop();
        looper.Start();
        Assert.True(looper.ArmRecord());
        Run(looper, 4.5, 0, new Dictionary<long, float> { [Rate * 2 + 3_000] = 0.3f });
        Assert.Equal(1, looper.LayerCount);                    // the old layer cleared, the new one recorded
        Assert.Equal(3_000, Array.FindIndex(looper.MixDown()!, v => v > 0.2f));
    }

    [Fact]
    public void Process_does_not_allocate()
    {
        var looper = new Looper(Rate, 120, 4, 1) { LatencySamples = 300 };
        looper.Start();
        var input = new float[Block];
        var output = new float[Block];
        looper.ArmRecord();
        Allocations.AssertNone(() => looper.Process(input, output));
    }

    [Fact]
    public void Calibration_measures_the_round_trip_and_says_when_it_hears_nothing()
    {
        foreach (var roundTrip in new[] { 300, 4_321 })
        {
            var probe = new LatencyProbe(Rate);
            var history = new List<float>();
            var input = new float[Block];
            var output = new float[Block];
            var rnd = new Random(1);
            while (!probe.IsDone)
            {
                for (var i = 0; i < Block; i++)
                {
                    var src = history.Count + i - roundTrip;
                    input[i] = (src >= 0 && src < history.Count ? history[src] * 0.15f : 0f) + (float)(rnd.NextDouble() - 0.5) * 0.002f;
                }
                probe.Process(input, output);
                history.AddRange(output);
            }
            var result = probe.Result();
            Assert.NotNull(result.LatencySamples);
            Assert.InRange(result.LatencySamples!.Value, roundTrip - 2, roundTrip + 3);
            Assert.Equal(LatencyProbe.Clicks, result.ClicksHeard);
        }

        var silent = LatencyProbe.Analyze(new float[Rate * 4], [Rate / 2, Rate, Rate * 3 / 2], Rate);
        Assert.Null(silent.LatencySamples);
        Assert.Contains("Heard only 0", silent.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_layer_has_its_own_gain_and_mute_in_playback_and_mix_down()
    {
        var looper = new Looper(Rate, 120, 4, 1) { ClickEnabled = false };
        var countIn = looper.CountInLength;
        var loop = looper.LoopLength;
        looper.Start();
        looper.ArmRecord();
        var notes = new Dictionary<long, float> { [countIn + 5_000] = 0.4f };
        var played = Run(looper, (countIn + 2 * loop) / (double)Rate, 0, notes);
        Assert.Equal(1, looper.LayerCount);

        looper.SetLayerGain(0, 0f);                           // muted
        played = Run(looper, loop / (double)Rate, 0, NoNotes);
        Assert.Equal(-1, FirstAbove(played, 0, 0.01f));
        looper.SetLayerGain(0, 0.5f);
        played = Run(looper, loop / (double)Rate, 0, NoNotes);
        Assert.InRange(played.Max(), 0.19f, 0.21f);
        Assert.InRange(looper.MixDown()!.Max(), 0.19f, 0.21f);
    }

    [Fact]
    public void The_bar_before_an_armed_layer_counts_in_even_with_the_click_off()
    {
        foreach (var countInToRecord in new[] { true, false })
        {
            var looper = new Looper(Rate, 120, 4, 2) { ClickEnabled = false, CountInBeforeRecording = countInToRecord };
            var countIn = looper.CountInLength;
            var bar = countIn;
            looper.Start();
            var armed = false;
            var played = Run(looper, (countIn + 2 * looper.LoopLength) / (double)Rate, 0, NoNotes, s =>
            {
                if (!armed && s > countIn + 1_000) armed = looper.ArmRecord();   // pass 1 records
            });

            Assert.True(armed);
            Assert.Equal(-1, FirstAbove(played.AsSpan(0, (int)(countIn + bar)).ToArray(), countIn, 0.05f)); // bar 1 of pass 0: quiet
            var countdown = FirstAbove(played, countIn + bar, 0.05f);
            if (countInToRecord)
            {
                Assert.InRange(countdown, countIn + bar, countIn + bar + 48);  // bar 2 clicks on its beat 1…
                for (var beat = 1; beat < 4; beat++)                           // …and every beat after
                    Assert.InRange(FirstAbove(played, countIn + bar + beat * Rate / 2, 0.05f), countIn + bar + beat * Rate / 2,
                        countIn + bar + beat * Rate / 2 + 48);
                Assert.Equal(-1, FirstAbove(played, countIn + looper.LoopLength + 2_000, 0.05f)); // quiet while it records
            }
            else
            {
                Assert.Equal(-1, countdown);
            }
        }
    }

    [Fact]
    public void A_layer_can_span_two_passes_and_plays_them_in_turn()
    {
        var looper = new Looper(Rate, 120, 4, 1) { ClickEnabled = false };
        var countIn = looper.CountInLength;
        var loop = looper.LoopLength;
        Assert.False(looper.ArmRecord(3));
        looper.Start();
        var armed = false;
        // Recorded over passes 1 and 2 (armed during pass 0): a different note in each.
        var notes = new Dictionary<long, float> { [countIn + loop + 5_000] = 0.3f, [countIn + 2 * loop + 20_000] = 0.3f };
        var played = Run(looper, (countIn + 5 * loop) / (double)Rate, 0, notes, s =>
        {
            if (!armed && s > countIn + 1_000) armed = looper.ArmRecord(2);
        });

        Assert.True(armed);
        Assert.Equal(1, looper.LayerCount);
        Assert.Equal(2, looper.LayerPasses(0));
        var pass3 = countIn + 3 * loop;
        Assert.Equal(pass3 + 5_000, FirstAbove(played, pass3, 0.25f));       // pass 3: the layer's first pass
        Assert.Equal(pass3 + loop + 20_000, FirstAbove(played, pass3 + 6_000, 0.25f)); // pass 4: its second
        var mix = looper.MixDown()!;
        Assert.Equal(2 * loop, mix.Length);
        Assert.Equal(5_000, Array.FindIndex(mix, v => v > 0.25f));

        // Restarted, the loop plays the long layer from its start.
        looper.Stop();
        Run(looper, 0.01, 0, NoNotes);
        looper.Start();
        played = Run(looper, (countIn + loop) / (double)Rate, 0, NoNotes);
        Assert.Equal(countIn + 5_000, FirstAbove(played, countIn, 0.25f));

        // A long loop can't take layers past five minutes.
        var longLoop = new Looper(Rate, 30, 7, 16);
        Assert.Equal(1, longLoop.MaxPassesPerLayer);
        Assert.False(longLoop.ArmRecord(2));
    }

    [Theory]
    [InlineData("Loopback 1 L/R (3- SSL 2 USB Au", "Loopback 1 L/R (3- SSL 2 USB Audio Device)", true)]
    [InlineData("Input 1 (SSL 2)", "Input 1 (SSL 2)", true)]
    [InlineData("Input", "Input 1 (3- SSL 2 USB Audio Device)", false)]
    [InlineData("Input 1 (3- SSL 2 USB Au", "Input 2 (3- SSL 2 USB Audio Device)", false)]
    public void The_same_input_is_found_on_another_driver_by_name(string a, string b, bool same)
    {
        Assert.Equal(same, PortAudioDuplex.SameDeviceName(a, b));
        Assert.Equal(same, PortAudioDuplex.SameDeviceName(b, a));
    }
}
