namespace GuitarPractice.Audio;

/// <summary>
/// Called on the audio thread with captured mono samples. Real-time rules apply:
/// no allocation, no locks, no I/O, no logging. Copy into a ring buffer and return.
/// </summary>
public delegate void AudioCaptureCallback(ReadOnlySpan<float> samples);

/// <summary>
/// Called on the audio thread to fill <paramref name="output"/> (mono, float32).
/// Same real-time rules as capture. The metronome counts samples here; it never uses timers.
/// </summary>
public delegate void AudioRenderCallback(Span<float> output);

/// <param name="Id">Opaque, valid for this run only (PortAudio index: changes when devices come and go).</param>
/// <param name="LatencySeconds">The driver's lowest suggested latency for this device (0 = unknown).</param>
/// <param name="Driver">Which audio driver (PortAudio host API) the device is reached through; with <see cref="Name"/>
/// it identifies the device across runs. -1 = unknown.</param>
public sealed record AudioDeviceInfo(
    string Id, string Name, int MaxChannels, double DefaultSampleRate, bool IsDefault, double LatencySeconds = 0, int Driver = -1);

/// <param name="Channel">Capture only: 0 = mix all input channels to mono, n = use input channel n (1-based).</param>
public sealed record AudioStreamOptions(string? DeviceId = null, int SampleRate = 48_000, int FramesPerBuffer = 256, int Channel = 0);

public interface IAudioInput : IDisposable
{
    IReadOnlyList<AudioDeviceInfo> Devices { get; }
    int SampleRate { get; }
    bool IsRunning { get; }

    /// <summary>The device's native rate; capture at it to avoid resampling.</summary>
    int GetPreferredSampleRate(string? deviceId = null);

    void Start(AudioStreamOptions options, AudioCaptureCallback callback);
    void Stop();
}

public interface IAudioOutput : IDisposable
{
    IReadOnlyList<AudioDeviceInfo> Devices { get; }
    int SampleRate { get; }

    /// <summary>Reported stream latency; the UI offsets beat flashes by this so they match what you hear.</summary>
    TimeSpan OutputLatency { get; }

    bool IsRunning { get; }

    /// <summary>The device's native rate; open the stream at it to avoid resampling. Prepare DSP for this rate.</summary>
    int GetPreferredSampleRate(string? deviceId = null);

    void Start(AudioStreamOptions options, AudioRenderCallback callback);
    void Stop();
}

/// <summary>
/// Creates audio streams. Each tool window owns (and disposes) its own input/output, so the metronome, tuner and
/// recorder can run at the same time without stopping each other's streams.
/// </summary>
public interface IAudioDevices
{
    IAudioInput CreateInput();
    IAudioOutput CreateOutput();
}

/// <summary>PortAudio-backed <see cref="IAudioDevices"/>.</summary>
public sealed class PortAudioDevices : IAudioDevices
{
    public IAudioInput CreateInput() => new PortAudioInput();
    public IAudioOutput CreateOutput() => new PortAudioOutput();
}

/// <summary>Pitch math shared by the tuner and the bend-accuracy mode.</summary>
public static class Pitch
{
    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>Fractional MIDI note: 69 + 12·log2(f / A4).</summary>
    public static double MidiFromFrequency(double hz, double a4 = 440.0) => 69.0 + 12.0 * Math.Log2(hz / a4);

    public static double FrequencyFromMidi(double midi, double a4 = 440.0) => a4 * Math.Pow(2.0, (midi - 69.0) / 12.0);

    /// <summary>Nearest note and the offset from it in cents (−50..+50).</summary>
    public static (int Midi, double Cents) Nearest(double hz, double a4 = 440.0)
    {
        var midi = MidiFromFrequency(hz, a4);
        var nearest = (int)Math.Round(midi);
        return (nearest, (midi - nearest) * 100.0);
    }

    public static string NoteName(int midi) => $"{Names[((midi % 12) + 12) % 12]}{(int)Math.Floor(midi / 12.0) - 1}";

    /// <summary>Pitch class without the octave ("F#").</summary>
    public static string PitchClassName(int midi) => Names[((midi % 12) + 12) % 12];

    /// <summary>Scientific pitch ("E2", "F#3", "Bb1", "C♯4") → MIDI (E2 = 40, A4 = 69). False when unparseable.</summary>
    public static bool TryParseNoteName(string? text, out int midi)
    {
        midi = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();

        var pitchClass = char.ToUpperInvariant(s[0]) switch
        {
            'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, 'B' => 11,
            _ => -1,
        };
        if (pitchClass < 0) return false;

        var i = 1;
        while (i < s.Length && s[i] is '#' or '♯' or 'b' or '♭')
        {
            pitchClass += s[i] is '#' or '♯' ? 1 : -1;
            i++;
        }

        if (!int.TryParse(s.AsSpan(i), System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var octave))
            return false;

        midi = (octave + 1) * 12 + pitchClass;
        return midi is >= 0 and <= 127;
    }
}
