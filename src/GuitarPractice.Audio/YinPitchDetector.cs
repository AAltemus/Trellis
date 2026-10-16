using System.Numerics;

namespace GuitarPractice.Audio;

/// <summary>One analysis frame. <see cref="Hz"/> is 0 when no pitch was found.</summary>
public readonly record struct PitchEstimate(double Hz, double Confidence)
{
    public bool IsVoiced => Hz > 0;
    public static PitchEstimate None => default;
}

/// <summary>
/// YIN pitch detector (de Cheveigné &amp; Kawahara 2002): difference function, cumulative-mean-normalized difference
/// (CMND), absolute threshold, then parabolic interpolation around the chosen lag. Confidence = 1 − CMND at that lag.
/// All buffers are allocated up front; <see cref="Detect"/> doesn't allocate. Not thread-safe (one per analysis thread).
/// </summary>
public sealed class YinPitchDetector
{
    private readonly float[] _difference;
    private readonly float[] _cmnd;

    /// <param name="sampleRate">Input rate.</param>
    /// <param name="windowSize">Integration window W; <see cref="RequiredSamples"/> = W + max lag.</param>
    /// <param name="minHz">Lowest detectable pitch (sets the max lag). 55 Hz covers B1 on 7-string / B standard.</param>
    /// <param name="maxHz">Highest detectable pitch (sets the min lag).</param>
    /// <param name="threshold">CMND threshold; 0.10–0.15 is typical for guitar.</param>
    public YinPitchDetector(int sampleRate, int windowSize = 2048, double minHz = 55, double maxHz = 1500, double threshold = 0.12)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 256);
        if (!(minHz > 0 && maxHz > minHz)) throw new ArgumentOutOfRangeException(nameof(minHz), "Need 0 < minHz < maxHz.");

        SampleRate = sampleRate;
        WindowSize = windowSize;
        Threshold = threshold;
        MinLag = Math.Max(2, (int)Math.Floor(sampleRate / maxHz));
        MaxLag = (int)Math.Ceiling(sampleRate / minHz);
        _difference = new float[MaxLag + 2];
        _cmnd = new float[MaxLag + 2];
    }

    public int SampleRate { get; }
    public int WindowSize { get; }
    public double Threshold { get; }
    public int MinLag { get; }
    public int MaxLag { get; }

    /// <summary>Samples <see cref="Detect"/> needs: the window plus the largest lag (+1 for interpolation).</summary>
    public int RequiredSamples => WindowSize + MaxLag + 1;

    /// <summary>Uses the last <see cref="RequiredSamples"/> samples of <paramref name="samples"/>.</summary>
    public PitchEstimate Detect(ReadOnlySpan<float> samples)
    {
        if (samples.Length < RequiredSamples) return PitchEstimate.None;
        var x = samples[^RequiredSamples..];
        var w = WindowSize;
        var maxLag = MaxLag + 1;

        // 1. Difference function d(τ) = Σ_j (x_j − x_{j+τ})².
        var head = x[..w];
        for (var tau = 1; tau <= maxLag; tau++)
            _difference[tau] = SquaredDistance(head, x.Slice(tau, w));

        // 2. Cumulative-mean-normalized difference: d'(τ) = d(τ)·τ / Σ_{k≤τ} d(k); d'(0) = 1.
        _cmnd[0] = 1;
        double running = 0;
        for (var tau = 1; tau <= maxLag; tau++)
        {
            running += _difference[tau];
            _cmnd[tau] = running > 0 ? (float)(_difference[tau] * tau / running) : 1f;
        }

        // 3. First lag under the threshold, then down to the bottom of that dip. Taking the first dip (not the
        //    global minimum) is what keeps YIN off sub-octaves.
        var best = -1;
        for (var tau = MinLag; tau < maxLag; tau++)
        {
            if (_cmnd[tau] < Threshold)
            {
                while (tau + 1 < maxLag && _cmnd[tau + 1] < _cmnd[tau]) tau++;
                best = tau;
                break;
            }
        }
        if (best < 0) return PitchEstimate.None; // aperiodic (noise, chord mush, silence)

        // 4. Parabolic interpolation on d' around the dip for sub-sample lag.
        var refined = (double)best;
        if (best > 1 && best < maxLag)
        {
            double a = _cmnd[best - 1], b = _cmnd[best], c = _cmnd[best + 1];
            var denominator = a - 2 * b + c;
            if (denominator > 0) refined = best + 0.5 * (a - c) / denominator;
        }

        return new PitchEstimate(SampleRate / refined, Math.Clamp(1 - _cmnd[best], 0, 1));
    }

    private static float SquaredDistance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var i = 0;
        var sum = 0f;
        if (Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            var acc = Vector<float>.Zero;
            var va = System.Runtime.InteropServices.MemoryMarshal.Cast<float, Vector<float>>(a);
            var vb = System.Runtime.InteropServices.MemoryMarshal.Cast<float, Vector<float>>(b);
            for (var v = 0; v < va.Length; v++)
            {
                var diff = va[v] - vb[v];
                acc += diff * diff;
            }
            sum = Vector.Sum(acc);
            i = va.Length * Vector<float>.Count;
        }
        for (; i < a.Length; i++)
        {
            var diff = a[i] - b[i];
            sum += diff * diff;
        }
        return sum;
    }
}
