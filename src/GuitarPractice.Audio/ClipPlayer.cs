namespace GuitarPractice.Audio;

/// <summary>One note for <see cref="PluckSynth"/>: start and length in seconds, MIDI pitch (A4 = 69 = 440 Hz).</summary>
public readonly record struct SynthNote(double Start, int Midi, double Duration);

/// <summary>
/// Plucked-string notes (Karplus–Strong) for the ear trainer: a noise burst circulating in a delay line with a two-point
/// average (string damping) and a first-order all-pass for the fractional part of the period, so every pitch is in tune
/// (within ~1 cent, measured in tests) instead of being rounded to a whole-sample delay. Fixed-seed noise: the same
/// note always renders the same samples.
/// </summary>
public static class PluckSynth
{
    /// <summary>Loudest sample of a rendered clip (chords and intervals end up equally loud).</summary>
    public const float Peak = 0.7f;

    private const double SustainSeconds = 3.0; // 60 dB decay time at A2; higher notes die faster (as on a guitar)
    private const double ReleaseSeconds = 0.05;

    public static float[] Render(int sampleRate, IEnumerable<SynthNote> notes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        ArgumentNullException.ThrowIfNull(notes);
        var list = notes.ToList();
        var length = list.Count == 0 ? 0 : (int)Math.Ceiling(list.Max(n => n.Start + n.Duration) * sampleRate);
        var mix = new double[length];
        var seed = 1u;
        foreach (var note in list)
            AddNote(mix, sampleRate, note, seed++);

        var max = 0.0;
        foreach (var v in mix) max = Math.Max(max, Math.Abs(v));
        var output = new float[length];
        for (var i = 0; i < length; i++) output[i] = max > 0 ? (float)(mix[i] / max * Peak) : 0f;
        return output;
    }

    private static void AddNote(double[] mix, int sampleRate, SynthNote note, uint seed)
    {
        var hz = Pitch.FrequencyFromMidi(note.Midi);
        var period = sampleRate / hz;                       // loop delay in samples
        var n = (int)Math.Floor(period - 0.6);              // whole-sample part; the average adds 0.5
        var d = period - n - 0.5;                           // all-pass delay, 0.1 ≤ d < 1.1
        var c = (1 - d) / (1 + d);
        var t60 = Math.Clamp(SustainSeconds * Math.Sqrt(110.0 / hz), 0.8, SustainSeconds); // seconds to fall 60 dB
        var feedback = Math.Pow(0.001, 1.0 / (t60 * hz));                                    // per trip round the loop

        // Excitation: noise, softened by a one-pole low-pass (a pick, not a hammer), DC removed.
        var ring = new double[n];
        var state = seed * 2_654_435_761u | 1;
        double lp = 0, mean = 0;
        for (var i = 0; i < n; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            lp += 0.5 * ((state / (double)uint.MaxValue * 2 - 1) - lp);
            ring[i] = lp;
            mean += lp;
        }
        mean /= n;
        for (var i = 0; i < n; i++) ring[i] -= mean;

        var start = (int)Math.Round(note.Start * sampleRate);
        var count = Math.Min((int)Math.Round(note.Duration * sampleRate), mix.Length - start);
        var release = Math.Min(count, (int)(ReleaseSeconds * sampleRate));
        var attack = Math.Max(1, (int)(0.002 * sampleRate));
        double previous = 0, apIn = 0, apOut = 0;
        var pos = 0;
        for (var i = 0; i < count; i++)
        {
            var current = ring[pos];
            var averaged = feedback * 0.5 * (current + previous);
            var shifted = c * averaged + apIn - c * apOut;   // fractional delay
            apIn = averaged;
            apOut = shifted;
            previous = current;
            ring[pos] = shifted;
            if (++pos == n) pos = 0;

            var gain = 1.0;
            if (i < attack) gain = (double)i / attack;
            if (i >= count - release) gain *= (double)(count - 1 - i) / release;
            mix[start + i] += current * gain;
        }
    }
}

/// <summary>
/// Plays one pre-rendered clip at a time; <see cref="Play"/> replaces whatever is playing. The audio thread only copies
/// samples: <see cref="Render"/> doesn't allocate, lock or block. The stream can stay open between clips (silence).
/// </summary>
public sealed class ClipPlayer
{
    /// <summary>One per Play call, so the audio thread can tell a replay of the same clip from the clip still playing.</summary>
    private sealed class Request(float[]? clip)
    {
        public float[]? Clip { get; } = clip;
    }

    private Request _request = new(null);
    private Request? _playing;
    private int _position;
    private float _volume = 1f;

    /// <summary>0–1.</summary>
    public float Volume
    {
        get => Volatile.Read(ref _volume);
        set => Volatile.Write(ref _volume, float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 1f);
    }

    /// <summary>True while a clip is sounding (read on the UI thread; approximate).</summary>
    public bool IsPlaying => Volatile.Read(ref _request).Clip is { } clip && Volatile.Read(ref _position) < clip.Length;

    /// <summary>Starts <paramref name="clip"/> from the beginning at the next buffer; null stops.</summary>
    public void Play(float[]? clip) => Volatile.Write(ref _request, new Request(clip));

    public void Stop() => Play(null);

    /// <summary>Audio thread: mono output.</summary>
    public void Render(Span<float> output)
    {
        var request = Volatile.Read(ref _request);
        var position = _position;
        if (!ReferenceEquals(request, _playing))
        {
            _playing = request;
            position = 0;
        }
        var clip = request.Clip;
        var volume = Volume;
        var i = 0;
        if (clip is not null)
            for (; i < output.Length && position < clip.Length; i++, position++)
                output[i] = clip[position] * volume;
        output[i..].Clear();
        Volatile.Write(ref _position, position);
    }
}
