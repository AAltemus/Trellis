using GuitarPractice.Audio;

namespace GuitarPractice.Audio.Tests;

public class TunerSmootherTests
{
    private static readonly int[] Standard = [40, 45, 50, 55, 59, 64]; // E2 A2 D3 G3 B3 E4

    private static PitchEstimate At(double hz) => new(hz, 0.95);

    private static TunerReading? Feed(TunerSmoother s, double hz, int frames)
    {
        TunerReading? r = null;
        for (var i = 0; i < frames; i++) r = s.Add(At(hz));
        return r;
    }

    [Fact]
    public void Needs_three_stable_frames_before_showing_a_note()
    {
        var s = new TunerSmoother();
        Assert.Null(s.Add(At(110)));
        Assert.Null(s.Add(At(110)));
        var r = s.Add(At(110));
        Assert.NotNull(r);
        Assert.Equal(45, r.TargetMidi); // A2
        Assert.InRange(r.Cents, -0.5, 0.5);
    }

    [Fact]
    public void A_single_glitch_frame_is_ignored()
    {
        var s = new TunerSmoother();
        Feed(s, 110, 5);
        var r = s.Add(At(220)); // octave glitch for one frame
        Assert.Equal(45, r!.TargetMidi);
        Assert.InRange(r.Hz, 109.9, 110.1); // median of 5 rejects it
    }

    [Fact]
    public void Switching_notes_takes_three_frames()
    {
        var s = new TunerSmoother();
        Feed(s, 110, 5);
        for (var i = 0; i < 4; i++) s.Add(At(146.83)); // median flips after 3 frames, then 3 more to switch
        var r = Feed(s, 146.83, 2);
        Assert.Equal(50, r!.TargetMidi); // D3
    }

    [Fact]
    public void Cents_are_measured_and_smoothed()
    {
        var s = new TunerSmoother();
        var sharp = 110 * Math.Pow(2, 12.0 / 1200); // +12 cents
        var r = Feed(s, sharp, 30);
        Assert.Equal(45, r!.TargetMidi);
        Assert.InRange(r.Cents, 11.9, 12.1);
    }

    [Fact]
    public void Silence_holds_briefly_then_clears()
    {
        var s = new TunerSmoother();
        Feed(s, 110, 5);
        for (var i = 0; i < TunerSmoother.HoldFrames - 1; i++) Assert.NotNull(s.Add(PitchEstimate.None));
        Assert.Null(s.Add(PitchEstimate.None));
        Assert.Null(s.Current);
    }

    [Fact]
    public void Tuning_mode_targets_the_closest_string_and_reports_large_offsets()
    {
        var s = new TunerSmoother { Strings = Standard };
        // Low E string tuned a whole tone down (D2 = 73.42 Hz) is still "string 0, −200 cents" in Standard...
        var r = Feed(s, 73.42, 10);
        Assert.Equal(40, r!.TargetMidi);
        Assert.Equal(0, r.StringIndex);
        Assert.InRange(r.Cents, -201, -199);

        // ...and in Drop D it's in tune.
        var dropD = new TunerSmoother { Strings = [38, 45, 50, 55, 59, 64] };
        var d = Feed(dropD, 73.42, 10);
        Assert.Equal((38, 0), (d!.TargetMidi, d.StringIndex));
        Assert.InRange(d.Cents, -1, 1);
    }

    [Fact]
    public void Chromatic_mode_has_no_string()
    {
        var s = new TunerSmoother();
        var r = Feed(s, 233.08, 10); // A#3
        Assert.Equal(58, r!.TargetMidi);
        Assert.Null(r.StringIndex);
    }

    [Fact]
    public void Reference_pitch_shifts_the_cents()
    {
        var s = new TunerSmoother { A4 = 432 };
        var r = Feed(s, 440, 10);
        Assert.Equal(69, r!.TargetMidi);
        Assert.InRange(r.Cents, 31.5, 32.0); // 1200·log2(440/432) = 31.77
    }

    [Theory]
    [InlineData(39.4, 0)]
    [InlineData(42.4, 0)]
    [InlineData(42.6, 1)]
    [InlineData(66.0, 5)]
    public void Closest_string(double midi, int expected) => Assert.Equal(expected, TunerSmoother.ClosestString(midi, Standard));
}

public class TunerEngineTests
{
    /// <summary>Delivers whatever the test pushes, in fixed-size chunks, like a capture callback.</summary>
    private sealed class FakeInput(int sampleRate) : IAudioInput
    {
        private AudioCaptureCallback? _callback;
        public IReadOnlyList<AudioDeviceInfo> Devices => [];
        public int SampleRate => sampleRate;
        public bool IsRunning => _callback is not null;
        public int GetPreferredSampleRate(string? deviceId = null) => sampleRate;
        public AudioStreamOptions? Options { get; private set; }

        public void Start(AudioStreamOptions options, AudioCaptureCallback callback)
        {
            Options = options;
            _callback = callback;
        }

        public void Stop() => _callback = null;
        public void Dispose() => Stop();

        public void Push(float[] samples, int chunk = 512)
        {
            for (var i = 0; i < samples.Length; i += chunk)
                _callback!(samples.AsSpan(i, Math.Min(chunk, samples.Length - i)));
        }
    }

    private static TunerState Run(TunerEngine engine, FakeInput input, float[] audio)
    {
        for (var i = 0; i < audio.Length; i += 512)
        {
            input.Push(audio[i..Math.Min(audio.Length, i + 512)]);
            while (engine.ProcessPending()) { }
        }
        return engine.State;
    }

    [Theory]
    [InlineData(48_000, 82.41, 40)]
    [InlineData(44_100, 110.0, 45)]
    [InlineData(44_100, 329.63, 64)]
    public void Detects_a_played_note_end_to_end(int rate, double hz, int midi)
    {
        var input = new FakeInput(rate);
        using var engine = new TunerEngine(input, backgroundAnalysis: false);
        engine.Start();

        var state = Run(engine, input, YinPitchDetectorTests.Saw(hz, rate, rate / 2)); // 0.5 s of a "string"

        Assert.NotNull(state.Reading);
        Assert.Equal(midi, state.Reading.TargetMidi);
        Assert.InRange(state.Reading.Cents, -3, 3);
        Assert.InRange(state.LevelDb, -30, 0);
    }

    [Fact]
    public void Quiet_input_is_gated()
    {
        var input = new FakeInput(48_000);
        using var engine = new TunerEngine(input, backgroundAnalysis: false);
        engine.Start();

        var state = Run(engine, input, YinPitchDetectorTests.Sine(110, 48_000, 24_000, amp: 0.0005)); // ≈ −69 dBFS
        Assert.Null(state.Reading);
        Assert.True(state.LevelDb < engine.GateDb);
    }

    [Fact]
    public void Dc_offset_and_rumble_are_filtered_out()
    {
        var input = new FakeInput(48_000);
        using var engine = new TunerEngine(input, backgroundAnalysis: false);
        engine.Start();

        var audio = YinPitchDetectorTests.Sine(146.83, 48_000, 24_000, amp: 0.2);
        var rumble = YinPitchDetectorTests.Sine(12, 48_000, 24_000, amp: 0.3);
        for (var i = 0; i < audio.Length; i++) audio[i] += rumble[i] + 0.25f;

        var state = Run(engine, input, audio);
        Assert.Equal(50, state.Reading!.TargetMidi);
        Assert.InRange(state.Reading.Cents, -2, 2);
    }

    [Fact]
    public void Analysis_runs_once_per_hop()
    {
        var input = new FakeInput(48_000);
        using var engine = new TunerEngine(input, backgroundAnalysis: false);
        engine.Start(channel: 2);
        Assert.Equal(2, input.Options!.Channel);

        input.Push(new float[4_096]); // enough for the first window
        Assert.True(engine.ProcessPending());
        Assert.False(engine.ProcessPending());
        input.Push(new float[TunerEngine.Hop - 1]);
        Assert.False(engine.ProcessPending());
        input.Push(new float[1]);
        Assert.True(engine.ProcessPending());
    }
}
