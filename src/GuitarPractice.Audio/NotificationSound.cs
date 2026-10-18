using System.Diagnostics;

namespace GuitarPractice.Audio;

/// <summary>Short notification sounds (timer finished). Fire-and-forget; never throws.</summary>
public interface INotificationSound
{
    void PlayDing();
}

/// <summary>Synthesizes notification sounds as 16-bit mono PCM WAV. No assets to ship.</summary>
public static class ToneSynth
{
    // Bell-like partials: (frequency ratio, amplitude, decay per second). The 2.76x partial is the
    // inharmonic "bell" component; higher partials die quickly so the attack sparkles and the tail is pure.
    private static readonly (double Ratio, double Amp, double Decay)[] BellPartials =
    [
        (1.00, 0.60, 2.2),
        (2.00, 0.25, 3.5),
        (2.76, 0.12, 5.0),
        (5.40, 0.05, 8.0),
    ];

    public static byte[] DingWav(int sampleRate = 48_000, double fundamentalHz = 880, double seconds = 1.6)
    {
        var samples = Bell(sampleRate, fundamentalHz, seconds);
        return ToWav(samples, sampleRate);
    }

    internal static float[] Bell(int sampleRate, double fundamentalHz, double seconds)
    {
        var n = (int)(sampleRate * seconds);
        var data = new float[n];
        var attack = (int)(0.005 * sampleRate); // 5 ms ramp avoids a click
        double peak = 0;

        for (var i = 0; i < n; i++)
        {
            var t = (double)i / sampleRate;
            double v = 0;
            foreach (var (ratio, amp, decay) in BellPartials)
                v += amp * Math.Exp(-decay * t) * Math.Sin(2 * Math.PI * fundamentalHz * ratio * t);
            if (i < attack) v *= (double)i / attack;
            data[i] = (float)v;
            peak = Math.Max(peak, Math.Abs(v));
        }

        var gain = peak > 0 ? 0.7 / peak : 1;
        for (var i = 0; i < n; i++) data[i] = (float)(data[i] * gain);
        return data;
    }

    internal static byte[] ToWav(float[] samples, int sampleRate)
    {
        const short channels = 1;
        const short bitsPerSample = 16;
        var dataBytes = samples.Length * channels * bitsPerSample / 8;

        using var ms = new MemoryStream(44 + dataBytes);
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8);
            w.Write(36 + dataBytes);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);                                         // PCM fmt chunk size
            w.Write((short)1);                                   // PCM
            w.Write(channels);
            w.Write(sampleRate);
            w.Write(sampleRate * channels * bitsPerSample / 8);  // byte rate
            w.Write((short)(channels * bitsPerSample / 8));      // block align
            w.Write(bitsPerSample);
            w.Write("data"u8);
            w.Write(dataBytes);
            foreach (var s in samples)
                w.Write((short)Math.Round(Math.Clamp(s, -1f, 1f) * short.MaxValue));
        }
        return ms.ToArray();
    }
}

/// <summary>
/// Plays the ding through PortAudio, the same output path as the metronome, so it's one code path on every OS (no
/// OS-specific players; user, 2026-10-18: "if it's cross platform i don't want anything platform specific"). The output
/// stream opens when a ding plays and closes shortly after it ends.
/// </summary>
public sealed class DeviceNotificationSound(IAudioDevices devices) : INotificationSound, IDisposable
{
    /// <summary>The ding is 1.6 s; the stream closes this long after the last one starts.</summary>
    public static readonly TimeSpan CloseAfter = TimeSpan.FromSeconds(2.2);

    private readonly Lock _gate = new();
    private readonly ClipPlayer _player = new();
    private IAudioOutput? _output;
    private float[]? _ding;
    private Timer? _closeTimer;
    private bool _disposed;

    public void PlayDing()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                _output ??= devices.CreateOutput();
                if (!_output.IsRunning)
                {
                    var rate = _output.GetPreferredSampleRate();
                    if (_ding is null || _rate != rate) (_ding, _rate) = (ToneSynth.Bell(rate, 880, 1.6), rate);
                    _output.Start(new AudioStreamOptions(SampleRate: rate, FramesPerBuffer: 256), _player.Render);
                }
                _player.Play(_ding);
                _closeTimer?.Dispose();
                _closeTimer = new Timer(_ => Close(), null, CloseAfter, Timeout.InfiniteTimeSpan);
            }
        }
#pragma warning disable CA1031 // A failed ding (no device, driver error) must never take down a practice session.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Notification sound failed: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    private int _rate;

    private void Close()
    {
        lock (_gate)
        {
            if (_player.IsPlaying) return; // a later ding is still sounding; its own timer closes the stream
            try
            {
                _output?.Stop();
            }
#pragma warning disable CA1031 // Closing the device must not throw on a timer thread.
            catch (Exception ex)
            {
                Trace.TraceWarning($"Closing the notification output failed: {ex.Message}");
            }
#pragma warning restore CA1031
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _closeTimer?.Dispose();
            _output?.Dispose();
            _output = null;
        }
    }
}
