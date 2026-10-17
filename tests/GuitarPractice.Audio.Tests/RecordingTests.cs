using System.Text;
using GuitarPractice.Audio;

namespace GuitarPractice.Audio.Tests;

public class WavFileTests
{
    [Fact]
    public void Round_trips_16_bit_mono()
    {
        var take = new AudioTake(YinPitchDetectorTests.Sine(220, 44_100, 10_000, amp: 0.8), 44_100);
        using var stream = new MemoryStream();
        WavFile.Write(stream, take);
        stream.Position = 0;

        var back = WavFile.Read(stream);

        Assert.Equal(44_100, back.SampleRate);
        Assert.Equal(take.Samples.Length, back.Samples.Length);
        for (var i = 0; i < take.Samples.Length; i++)
            Assert.True(Math.Abs(take.Samples[i] - back.Samples[i]) <= 0.5 / 32768 + 1e-9, $"sample {i}: {take.Samples[i]} vs {back.Samples[i]}"); // ≤ half a 16-bit step
        Assert.Equal(44 + 2 * 10_000, stream.Length);
    }

    private static byte[] Wav(short format, short channels, int rate, short bits, byte[] data, bool extraChunk = false)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(0);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write(format);
        w.Write(channels);
        w.Write(rate);
        w.Write(rate * channels * bits / 8);
        w.Write((short)(channels * bits / 8));
        w.Write(bits);
        if (extraChunk)
        {
            w.Write("LIST"u8);
            w.Write(3);
            w.Write(new byte[] { 1, 2, 3, 0 }); // odd size + pad byte
        }
        w.Write("data"u8);
        w.Write(data.Length);
        w.Write(data);
        return ms.ToArray();
    }

    [Fact]
    public void Reads_stereo_float_and_mixes_to_mono()
    {
        var data = new byte[4 * 4]; // 2 frames × 2 channels × float
        BitConverter.GetBytes(0.5f).CopyTo(data, 0);
        BitConverter.GetBytes(-0.1f).CopyTo(data, 4);
        BitConverter.GetBytes(1.0f).CopyTo(data, 8);
        BitConverter.GetBytes(0.0f).CopyTo(data, 12);

        var take = WavFile.Read(new MemoryStream(Wav(3, 2, 48_000, 32, data, extraChunk: true)));

        Assert.Equal(48_000, take.SampleRate);
        Assert.Equal([0.2f, 0.5f], take.Samples.Select(s => MathF.Round(s, 5)));
    }

    [Fact]
    public void Reads_24_bit_pcm()
    {
        byte[] data = [0x00, 0x00, 0x40, 0x00, 0x00, 0xC0]; // +0.5, −0.5
        var take = WavFile.Read(new MemoryStream(Wav(1, 1, 44_100, 24, data)));
        Assert.Equal([0.5f, -0.5f], take.Samples);
    }

    [Fact]
    public void Rejects_non_wav_data()
    {
        Assert.Throws<InvalidDataException>(() => WavFile.Read(new MemoryStream(Encoding.ASCII.GetBytes("not a wave file at all"))));
    }
}

public class WaveformPeaksTests
{
    [Fact]
    public void Min_and_max_per_column()
    {
        float[] x = [0, 1, -1, 0.5f, -0.25f, 0.25f, 0, 0];
        var peaks = WaveformPeaks.Compute(x, 4);
        Assert.Equal([0f, -1f, -0.25f, 0f], peaks.Min);
        Assert.Equal([1f, 0.5f, 0.25f, 0f], peaks.Max);
    }

    [Fact]
    public void More_columns_than_samples_and_empty_input_work()
    {
        Assert.Equal(10, WaveformPeaks.Compute([0.5f, -0.5f], 10).Max.Length);
        Assert.All(WaveformPeaks.Compute([], 5).Max, v => Assert.Equal(0f, v));
    }
}

public class TakeRecorderTests
{
    private sealed class PushInput(int rate) : IAudioInput
    {
        private AudioCaptureCallback? _callback;
        public IReadOnlyList<AudioDeviceInfo> Devices => [];
        public int SampleRate => rate;
        public bool IsRunning => _callback is not null;
        public int GetPreferredSampleRate(string? deviceId = null) => rate;
        public void Start(AudioStreamOptions options, AudioCaptureCallback callback) => _callback = callback;
        public void Stop() => _callback = null;
        public void Dispose() => Stop();
        public void Push(ReadOnlySpan<float> samples) => _callback?.Invoke(samples);
    }

    [Fact]
    public void Records_exactly_what_was_captured()
    {
        var input = new PushInput(44_100);
        using var recorder = new TakeRecorder(input, backgroundDrain: false);
        recorder.Start();
        var audio = YinPitchDetectorTests.Sine(196, 44_100, 44_100 * 3);
        for (var i = 0; i < audio.Length; i += 512)
        {
            input.Push(audio.AsSpan(i, Math.Min(512, audio.Length - i)));
            if (i % 8192 == 0) recorder.Drain(); // drains lag behind capture, like the real thread
        }

        Assert.True(recorder.PeakDb > -7, $"peak {recorder.PeakDb:0.0} dBFS"); // 0.5 amplitude ≈ −6 dBFS
        var take = recorder.Stop();

        Assert.Equal(44_100, take.SampleRate);
        Assert.Equal(audio, take.Samples);
        Assert.Equal(0, recorder.DroppedSamples);
        Assert.Equal(TimeSpan.FromSeconds(3), take.Duration);
    }

    [Fact]
    public void A_stalled_drain_drops_the_oldest_audio_and_counts_it()
    {
        var input = new PushInput(48_000);
        using var recorder = new TakeRecorder(input, backgroundDrain: false);
        recorder.Start();
        var block = new float[512];
        for (var i = 0; i < 600; i++) input.Push(block); // 307 200 samples > 262 144-sample ring, no drain

        var take = recorder.Stop();
        Assert.Equal(600 * 512 - (1 << 18), recorder.DroppedSamples);
        Assert.Equal(1 << 18, take.Samples.Length);
    }

    [Fact]
    public void Can_record_again_after_stopping()
    {
        var input = new PushInput(48_000);
        using var recorder = new TakeRecorder(input, backgroundDrain: false);
        recorder.Start();
        input.Push(new float[1000]);
        Assert.Equal(1000, recorder.Stop().Samples.Length);

        recorder.Start();
        input.Push(new float[300]);
        Assert.Equal(300, recorder.Stop().Samples.Length);
        Assert.False(recorder.IsRecording);
    }
}

public class LoopPlayerTests
{
    private const int Rate = 48_000;

    private static float[] Render(LoopPlayer player, int samples, int chunk = 256)
    {
        var output = new float[samples];
        for (var i = 0; i < samples; i += chunk) player.Render(output.AsSpan(i, Math.Min(chunk, samples - i)));
        return output;
    }

    private static AudioTake Ramp(int length, int rate = Rate)
    {
        var x = new float[length];
        for (var i = 0; i < length; i++) x[i] = (i % 1000) / 2000f; // distinct, easy-to-check values
        return new AudioTake(x, rate);
    }

    [Fact]
    public void Plays_the_take_exactly_then_stops()
    {
        var take = Ramp(10_000);
        var player = new LoopPlayer(Rate);
        player.Load(take);
        player.Play();

        var output = Render(player, 12_000);

        Assert.Equal(take.Samples, output[..10_000]);
        Assert.All(output[10_000..], v => Assert.Equal(0f, v));
        Assert.False(player.IsPlaying);
        Assert.Equal(10_000 / (double)Rate, player.PositionSeconds, 6);
    }

    [Fact]
    public void Pause_holds_and_play_resumes_where_it_left_off()
    {
        var take = Ramp(10_000);
        var player = new LoopPlayer(Rate);
        player.Load(take);
        player.Play();
        var first = Render(player, 3_000);
        player.Pause();
        Assert.All(Render(player, 1_000), v => Assert.Equal(0f, v));
        player.Play();
        var second = Render(player, 2_000);

        Assert.Equal(take.Samples[..3_000], first);
        Assert.Equal(take.Samples[3_000..5_000], second);
    }

    [Fact]
    public void Seek_moves_the_playhead()
    {
        var take = Ramp(48_000);
        var player = new LoopPlayer(Rate);
        player.Load(take);
        player.Seek(0.5);
        player.Play();
        Assert.Equal(take.Samples[24_000..24_500], Render(player, 500));
    }

    [Fact]
    public void Loops_exactly_every_B_minus_A_samples()
    {
        // Loop 12 345 .. 40 000 (27 655 samples). After the first pass the output must repeat with exactly that period.
        var take = new AudioTake(YinPitchDetectorTests.Saw(110, Rate, Rate, noise: 0), Rate);
        var player = new LoopPlayer(Rate) { Settings = new PlayerSettings(LoopEnabled: true, LoopStart: 12_345.0 / Rate, LoopEnd: 40_000.0 / Rate) };
        player.Load(take);
        player.Seek(12_345.0 / Rate);
        player.Play();

        const int period = 40_000 - 12_345;
        var output = Render(player, period * 5);

        for (var n = period; n < period * 4; n++) Assert.Equal(output[n], output[n + period], 5);
        Assert.Equal(5, player.LoopCount); // the 5th wrap happens on the last rendered sample
        // Away from the seam it's the take itself.
        Assert.Equal(take.Samples[13_000..39_000], output[(13_000 - 12_345)..(39_000 - 12_345)]);
    }

    [Fact]
    public void The_loop_seam_is_smooth()
    {
        // A sine whose loop length is not a whole number of periods: a hard jump would step by up to ~1.0.
        var take = new AudioTake(YinPitchDetectorTests.Sine(220, Rate, Rate, amp: 0.5), Rate);
        var player = new LoopPlayer(Rate) { Settings = new PlayerSettings(LoopEnabled: true, LoopStart: 0.1013, LoopEnd: 0.3471) };
        player.Load(take);
        player.Seek(0.1013);
        player.Play();
        var output = Render(player, Rate * 2);

        var maxStep = 0.0;
        for (var i = 1; i < output.Length; i++) maxStep = Math.Max(maxStep, Math.Abs(output[i] - output[i - 1]));
        var sineStep = 2 * Math.PI * 220 * 0.5 / Rate; // steepest sample-to-sample change of the sine itself
        Assert.True(maxStep < sineStep * 1.6, $"max step {maxStep:0.0000} vs sine {sineStep:0.0000}");
        Assert.True(player.LoopCount >= 7);
    }

    [Fact]
    public void Play_outside_the_loop_starts_at_A()
    {
        var take = Ramp(48_000);
        var player = new LoopPlayer(Rate) { Settings = new PlayerSettings(LoopEnabled: true, LoopStart: 0.25, LoopEnd: 0.75) };
        player.Load(take);
        player.Seek(0.9);
        player.Play();
        Assert.Equal(take.Samples[12_000..12_100], Render(player, 100));
    }

    [Fact]
    public void Count_in_clicks_on_the_beat_then_playback_starts()
    {
        var take = Ramp(10_000);
        var player = new LoopPlayer(Rate) { Settings = new PlayerSettings(CountIn: true, CountInBpm: 120, CountInBeats: 4) };
        player.Load(take);
        player.Play();
        Assert.True(player.IsPlaying);

        var output = Render(player, 4 * 24_000 + 10_000);

        foreach (var beat in new[] { 0, 24_000, 48_000, 72_000 })
        {
            Assert.True(output.AsSpan(beat, 200).ToArray().Max(Math.Abs) > 0.1f, $"no click at {beat}");
            Assert.Equal(0f, output[beat + 5_000]);
        }
        Assert.Equal(take.Samples, output[96_000..]);
        Assert.False(player.IsCountingIn);
    }

    [Fact]
    public void Resamples_when_the_output_rate_differs()
    {
        var take = new AudioTake(YinPitchDetectorTests.Sine(440, 44_100, 44_100), 44_100); // 1 s at 44.1 kHz
        var player = new LoopPlayer(48_000);
        player.Load(take);
        player.Play();
        var output = Render(player, 50_000);

        var played = Array.FindLastIndex(output, v => v != 0) + 1;
        Assert.InRange(played, 47_990, 48_001); // 1 s at the output rate (±1 from accumulating the 44.1/48 step)
        var estimate = new YinPitchDetector(48_000).Detect(output.AsSpan(0, 20_000));
        Assert.InRange(1200 * Math.Log2(estimate.Hz / 440), -1, 1); // pitch preserved
    }

    [Fact]
    public void Volume_boost_is_soft_limited()
    {
        var take = new AudioTake(YinPitchDetectorTests.Sine(220, Rate, 4_800, amp: 0.9), Rate);
        var player = new LoopPlayer(Rate) { Settings = new PlayerSettings(Volume: 2f) };
        player.Load(take);
        player.Play();
        Assert.True(Render(player, 4_800).Max(Math.Abs) <= 1f);
    }

    [Fact]
    public void Render_does_not_allocate()
    {
        var take = Ramp(Rate);
        var player = new LoopPlayer(Rate) { Settings = new PlayerSettings(LoopEnabled: true, LoopStart: 0.1, LoopEnd: 0.4, CountIn: true) };
        player.Load(take);
        player.Play();
        var buffer = new float[256];
        player.Render(buffer);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 2_000; i++) player.Render(buffer);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
