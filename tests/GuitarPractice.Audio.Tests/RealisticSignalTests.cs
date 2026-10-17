using GuitarPractice.Audio;

namespace GuitarPractice.Audio.Tests;

/// <summary>
/// End-to-end tuner and bend-trainer behaviour on signals shaped like a DI'd guitar through an audio interface:
/// inharmonic partials, exponential decay (higher partials faster), a noise floor, and a modest peak level
/// (−30 dBFS, i.e. interface gain not cranked). Regression tests for the user report (2026-10-16):
/// "bend trainer keeps cutting signal too quick and not registering the bend; same thing happened with the tuner".
/// </summary>
public class RealisticSignalTests
{
    private const int Rate = 44_100; // the SSL 2 runs at 44.1 kHz

    /// <summary>Feeds pushed audio to the capture callback, like a PortAudio input.</summary>
    private sealed class PushInput : IAudioInput
    {
        private AudioCaptureCallback? _callback;
        public IReadOnlyList<AudioDeviceInfo> Devices => [];
        public int SampleRate => Rate;
        public bool IsRunning => _callback is not null;
        public int GetPreferredSampleRate(string? deviceId = null) => Rate;
        public void Start(AudioStreamOptions options, AudioCaptureCallback callback) => _callback = callback;
        public void Stop() => _callback = null;
        public void Dispose() => Stop();
        public void Push(ReadOnlySpan<float> samples) => _callback!(samples);
    }

    /// <summary>
    /// A plucked string whose pitch follows <paramref name="midiAt"/> (seconds → fractional MIDI). 60 dB decay over
    /// <paramref name="decaySeconds"/>, peak about <paramref name="peakDb"/> dBFS, white noise at <paramref name="noiseDb"/>.
    /// </summary>
    internal static float[] Pluck(Func<double, double> midiAt, double seconds, double peakDb = -30, double decaySeconds = 4,
        double noiseDb = -85, int seed = 3)
    {
        var n = (int)(seconds * Rate);
        var x = new float[n];
        var rng = new Random(seed);
        const int partials = 12;
        const double inharmonicity = 0.0001;
        var phases = new double[partials + 1];
        var tau = decaySeconds / Math.Log(1000);
        var gain = Math.Pow(10, peakDb / 20) / 1.6; // partial amplitudes 1/k sum to ~3; normalise to the peak roughly
        var noise = Math.Pow(10, noiseDb / 20) * Math.Sqrt(3);
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / Rate;
            var f0 = Pitch.FrequencyFromMidi(midiAt(t));
            double v = 0;
            for (var k = 1; k <= partials; k++)
            {
                var fk = k * f0 * Math.Sqrt(1 + inharmonicity * k * k);
                if (fk > Rate / 2.2) break;
                phases[k] += 2 * Math.PI * fk / Rate;
                v += Math.Exp(-t * (1 + 0.25 * k) / tau) / k * Math.Sin(phases[k]);
            }
            x[i] = (float)(gain * v + noise * (rng.NextDouble() * 2 - 1));
        }
        return x;
    }

    /// <summary>Runs audio through a TunerEngine in 512-sample callbacks; returns (time of each analysis frame, state).</summary>
    private static List<(double Seconds, TunerState State)> Analyze(float[] audio)
    {
        var input = new PushInput();
        using var engine = new TunerEngine(input, backgroundAnalysis: false);
        engine.Start();
        var frames = new List<(double, TunerState)>();
        for (var i = 0; i < audio.Length; i += 512)
        {
            input.Push(audio.AsSpan(i, Math.Min(512, audio.Length - i)));
            while (engine.ProcessPending()) frames.Add(((double)(i + 512) / Rate, engine.State));
        }
        return frames;
    }

    [Fact]
    public void A_ringing_note_at_modest_level_keeps_reading_for_most_of_its_decay()
    {
        const int a2 = 45;
        var frames = Analyze(Pluck(_ => a2, seconds: 5));

        var voiced = frames.Where(f => f.State.Reading is not null).ToList();
        Assert.NotEmpty(voiced);
        var lastVoiced = voiced.Max(f => f.Seconds);
        var peak = frames.Max(f => f.State.LevelDb);
        var atStop = frames.First(f => f.Seconds > lastVoiced).State.LevelDb;
        // The reading must last until the note has decayed to the gate's close level (40 dB under the peak, floor
        // −76 dBFS), not stop while it's still clearly ringing (the old −54 dBFS gate cut it at 1.11 s, ~20 dB too early).
        var closeLevel = Math.Max(-76, peak - 40);
        Assert.True(atStop <= closeLevel + 3, $"reading stopped at {lastVoiced:0.00} s while the level was {atStop:0.0} dBFS (peak {peak:0.0})");
        Assert.True(lastVoiced > 2.0, $"reading stopped at {lastVoiced:0.00} s");
        // ...without gaps while it's ringing.
        var dropouts = frames.Count(f => f.Seconds > 0.2 && f.Seconds < lastVoiced && f.State.Reading is null);
        Assert.Equal(0, dropouts);
        Assert.All(voiced, f => Assert.Equal(a2, f.State.Reading!.TargetMidi));
    }

    [Fact]
    public void Idle_noise_and_hum_do_not_produce_readings()
    {
        // Interface noise floor plus mains hum (60 Hz + harmonics) at −70 dBFS, no note.
        var n = Rate * 3;
        var x = Pluck(_ => 45, seconds: 3, peakDb: -200); // just the −85 dBFS noise
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / Rate;
            x[i] += (float)(0.0003 * (Math.Sin(2 * Math.PI * 60 * t) + 0.5 * Math.Sin(2 * Math.PI * 120 * t) + 0.3 * Math.Sin(2 * Math.PI * 180 * t)));
        }
        Assert.All(Analyze(x), f => Assert.Null(f.State.Reading));
    }

    [Fact]
    public void A_whole_step_bend_is_tracked_and_scored()
    {
        // Fret G3, let it ring 0.3 s, bend up a whole step over 0.2 s, hold 1.2 s at +5 ¢, release over 0.2 s.
        const double g3 = 55;
        double Midi(double t) => t switch
        {
            < 0.3 => g3,
            < 0.5 => g3 + 2.05 * (t - 0.3) / 0.2,
            < 1.7 => g3 + 2.05,
            < 1.9 => g3 + 2.05 * (1 - (t - 1.7) / 0.2),
            _ => g3,
        };
        var frames = Analyze(Pluck(Midi, seconds: 2.4));

        var tracker = new BendTracker { IntervalSemitones = 2 };
        var attempts = new List<BendAttempt>();
        var phases = new List<BendPhase>();
        foreach (var (seconds, state) in frames)
        {
            var bend = tracker.Update(state.Reading?.Hz, TimeSpan.FromSeconds(seconds));
            phases.Add(bend.Phase);
            if (bend.Completed is { } done) attempts.Add(done);
        }

        Assert.Contains(BendPhase.Holding, phases);
        var attempt = Assert.Single(attempts);
        Assert.Equal((55, 57), (attempt.ReferenceMidi, attempt.TargetMidi));
        Assert.InRange(attempt.AverageCents, 0, 10);
        Assert.True(attempt.Held > TimeSpan.FromSeconds(0.8), $"held {attempt.Held.TotalSeconds:0.00} s");
    }

    [Fact]
    public void Save_recording_writes_the_raw_input_as_a_wav()
    {
        var input = new PushInput();
        using var engine = new TunerEngine(input, backgroundAnalysis: false);
        engine.Start();
        var audio = Pluck(_ => 45, seconds: 3, peakDb: -12);
        input.Push(audio);

        var path = Path.Combine(Path.GetTempPath(), $"tuner-test-{Guid.NewGuid():N}.wav");
        try
        {
            var count = engine.SaveRecording(path, seconds: 2);
            Assert.Equal(2 * Rate, count);
            var wav = File.ReadAllBytes(path);
            Assert.Equal(44 + count * 2, wav.Length);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal(Rate, BitConverter.ToInt32(wav, 24));
            // The last sample is the last raw (unfiltered) input sample, quantised to 16 bits.
            var last = BitConverter.ToInt16(wav, wav.Length - 2) / (double)short.MaxValue;
            Assert.Equal(audio[^1], last, 3);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
