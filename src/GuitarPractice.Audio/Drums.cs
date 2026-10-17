namespace GuitarPractice.Audio;

/// <summary>What the metronome plays on each step.</summary>
public enum MetronomeSound
{
    /// <summary>The beep click (accent / beat / subdivision tones).</summary>
    Click = 0,

    /// <summary>The drum pattern in <see cref="MetronomeSettings.Drums"/>.</summary>
    Drums = 1,
}

/// <summary>The drums hit on one step of a pattern.</summary>
[Flags]
public enum DrumHits
{
    None = 0,
    Kick = 1,
    Snare = 2,
    HiHat = 4,
    All = Kick | Snare | HiHat,
}

/// <summary>
/// A one-bar drum pattern on a grid of <see cref="BeatsPerBar"/> × <see cref="Subdivision"/> steps; step
/// beat·stepsPerBeat + n is the n-th subdivision of that beat. Immutable by convention: <see cref="Steps"/> is never
/// changed after the pattern is published (edits make a new pattern), so the audio thread can read it without locks.
/// Equality compares the steps, not the array reference.
/// </summary>
public sealed record DrumPattern(string Name, int BeatsPerBar, Subdivision Subdivision, DrumHits[] Steps)
{
    public const int MaxNameLength = 40;

    public int StepsPerBeat => (int)Subdivision;

    public int StepCount => BeatsPerBar * StepsPerBeat;

    /// <summary>Hi-hat on every 8th, kick on 1 and 3, snare on 2 and 4.</summary>
    public static DrumPattern BasicRock { get; } = Parse("Basic rock", 4, Subdivision.Eighths,
        "KH H SH H KH H SH H");

    /// <summary>Built-in patterns; they can be picked and edited, and saved under another name.</summary>
    public static IReadOnlyList<DrumPattern> BuiltIns { get; } =
    [
        BasicRock,
        Parse("Four on the floor", 4, Subdivision.Eighths, "KH H KSH H KH H KSH H"),
        Parse("16th hats", 4, Subdivision.Sixteenths, "KH H H H SH H H H KH H KH H SH H H H"),
        Parse("Shuffle", 4, Subdivision.Triplets, "KH - H SH - H KH - H SH - H"),
        Parse("Waltz", 3, Subdivision.Quarters, "KH H SH"),
        Parse("Just hats", 4, Subdivision.Eighths, "H H H H H H H H"),
    ];

    /// <summary>
    /// The hits on (beat, subTick) of a bar played at <paramref name="ticksPerBeat"/> ticks per beat, even when that grid
    /// differs from the pattern's: a tick that falls exactly on one of the pattern's steps gets that step's hits; ticks
    /// in between get none; beats past the pattern's length repeat it. Doesn't allocate (audio thread).
    /// </summary>
    public DrumHits At(int beat, int subTick, int ticksPerBeat)
    {
        if (BeatsPerBar <= 0 || ticksPerBeat <= 0 || Steps is null) return DrumHits.None;
        var scaled = subTick * StepsPerBeat;
        if (scaled % ticksPerBeat != 0) return DrumHits.None;
        var index = beat % BeatsPerBar * StepsPerBeat + scaled / ticksPerBeat;
        return index < Steps.Length ? Steps[index] & DrumHits.All : DrumHits.None;
    }

    /// <summary>This pattern on another grid (see <see cref="At"/>): extra beats repeat the pattern.</summary>
    public DrumPattern Resize(int beatsPerBar, Subdivision subdivision)
    {
        var perBeat = (int)subdivision;
        var steps = new DrumHits[beatsPerBar * perBeat];
        for (var b = 0; b < beatsPerBar; b++)
            for (var n = 0; n < perBeat; n++)
                steps[b * perBeat + n] = At(b, n, perBeat);
        return this with { BeatsPerBar = beatsPerBar, Subdivision = subdivision, Steps = steps };
    }

    /// <summary>A copy with step <paramref name="index"/> set to <paramref name="hits"/>.</summary>
    public DrumPattern WithStep(int index, DrumHits hits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Steps.Length);
        var steps = (DrumHits[])Steps.Clone();
        steps[index] = hits & DrumHits.All;
        return this with { Steps = steps };
    }

    /// <summary>Trimmed, capped name; grid within the metronome's limits; exactly <see cref="StepCount"/> steps.</summary>
    public DrumPattern Normalized()
    {
        var name = (Name ?? "").Trim();
        if (name.Length > MaxNameLength) name = name[..MaxNameLength].TrimEnd();
        var beats = Math.Clamp(BeatsPerBar, 1, MetronomeSettings.MaxBeatsPerBar);
        var subdivision = Enum.IsDefined(Subdivision) ? Subdivision : Subdivision.Quarters;
        var steps = new DrumHits[beats * (int)subdivision];
        var source = Steps ?? [];
        for (var i = 0; i < steps.Length && i < source.Length; i++) steps[i] = source[i] & DrumHits.All;
        return new DrumPattern(name, beats, subdivision, steps);
    }

    public bool Equals(DrumPattern? other) =>
        other is not null && Name == other.Name && BeatsPerBar == other.BeatsPerBar && Subdivision == other.Subdivision
        && (Steps ?? []).AsSpan().SequenceEqual(other.Steps ?? []);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        hash.Add(BeatsPerBar);
        hash.Add(Subdivision);
        foreach (var s in Steps ?? []) hash.Add(s);
        return hash.ToHashCode();
    }

    /// <summary>Steps separated by spaces; each step lists K (kick), S (snare), H (hi-hat); "-" = rest.</summary>
    internal static DrumPattern Parse(string name, int beatsPerBar, Subdivision subdivision, string steps)
    {
        var parsed = steps.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(step =>
            (step.Contains('K') ? DrumHits.Kick : 0) | (step.Contains('S') ? DrumHits.Snare : 0) | (step.Contains('H') ? DrumHits.HiHat : 0)).ToArray();
        if (parsed.Length != beatsPerBar * (int)subdivision)
            throw new ArgumentException($"{name}: {parsed.Length} steps, expected {beatsPerBar * (int)subdivision}", nameof(steps));
        return new DrumPattern(name, beatsPerBar, subdivision, parsed);
    }
}

/// <summary>
/// Synthesised drum one-shots, pre-rendered once per sample rate so the audio thread only copies samples. Noise comes
/// from a fixed-seed generator, so every hit (and every test) is identical.
/// <list type="bullet">
/// <item>Kick: sine with a pitch drop 150 → 45 Hz (35 ms) and a 150 ms body, plus a 4 ms beater click; 400 ms.</item>
/// <item>Snare: 180 + 330 Hz shell tones (30 ms decay) under high-passed noise (60 ms decay); 250 ms.</item>
/// <item>Closed hi-hat: noise high-passed twice at 7 kHz, 15 ms decay; 70 ms.</item>
/// </list>
/// </summary>
internal static class DrumSynth
{
    public const float KickPeak = 1.0f;
    public const float SnarePeak = 0.8f;
    public const float HiHatPeak = 0.5f;

    public static float[] Kick(int sampleRate)
    {
        var n = (int)(sampleRate * 0.4);
        var data = new double[n];
        var noise = new Noise(1);
        var phase = 0.0;
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / sampleRate;
            var hz = 45 + 105 * Math.Exp(-t / 0.035);
            phase += 2 * Math.PI * hz / sampleRate;
            data[i] = Math.Sin(phase) * Math.Exp(-t / 0.15) + 0.3 * noise.Next() * Math.Exp(-t / 0.004);
        }
        return Finish(data, sampleRate, KickPeak);
    }

    public static float[] Snare(int sampleRate)
    {
        var n = (int)(sampleRate * 0.25);
        var data = new double[n];
        var noise = new Noise(2);
        var highPass = new HighPass(sampleRate, 1_200);
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / sampleRate;
            var shell = (Math.Sin(2 * Math.PI * 180 * t) + 0.6 * Math.Sin(2 * Math.PI * 330 * t)) * Math.Exp(-t / 0.03);
            data[i] = 0.5 * shell + highPass.Process(noise.Next()) * Math.Exp(-t / 0.06);
        }
        return Finish(data, sampleRate, SnarePeak);
    }

    public static float[] HiHat(int sampleRate)
    {
        var n = (int)(sampleRate * 0.07);
        var data = new double[n];
        var noise = new Noise(3);
        var cutoff = Math.Min(7_000, sampleRate * 0.4);
        var first = new HighPass(sampleRate, cutoff);
        var second = new HighPass(sampleRate, cutoff);
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / sampleRate;
            data[i] = second.Process(first.Process(noise.Next())) * Math.Exp(-t / 0.015);
        }
        return Finish(data, sampleRate, HiHatPeak);
    }

    /// <summary>0.3 ms fade-in, 3 ms fade-out to exactly zero, loudest sample = <paramref name="peak"/>.</summary>
    private static float[] Finish(double[] data, int sampleRate, float peak)
    {
        var n = data.Length;
        var attack = Math.Max(1, (int)(0.0003 * sampleRate));
        var release = Math.Max(1, (int)(0.003 * sampleRate));
        for (var i = 0; i < attack; i++) data[i] *= (double)i / attack;
        for (var i = n - release; i < n; i++) data[i] *= (double)(n - 1 - i) / release;
        var max = data.Max(Math.Abs);
        var result = new float[n];
        for (var i = 0; i < n; i++) result[i] = max > 0 ? (float)(data[i] / max * peak) : 0f;
        return result;
    }

    /// <summary>xorshift32 white noise in [-1, 1).</summary>
    private struct Noise(uint seed)
    {
        private uint _state = seed * 2_654_435_761u | 1;

        public double Next()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return _state / (double)uint.MaxValue * 2 - 1;
        }
    }

    /// <summary>One-pole high-pass.</summary>
    private sealed class HighPass(int sampleRate, double cutoffHz)
    {
        private readonly double _a = 1 / (1 + 2 * Math.PI * cutoffHz / sampleRate);
        private double _x;
        private double _y;

        public double Process(double x)
        {
            _y = _a * (_y + x - _x);
            _x = x;
            return _y;
        }
    }
}
