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
/// When no lag dips under the threshold (a decaying or gliding note, e.g. mid-bend), the paper's fallback applies: the
/// global CMND minimum is used if it is below <see cref="AperiodicityLimit"/>, after checking the integer fractions of
/// that lag so the fallback can't land on a sub-octave.
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
    /// <param name="aperiodicityLimit">Fallback: accept the global CMND minimum below this (noise sits around 0.6–1).</param>
    public YinPitchDetector(int sampleRate, int windowSize = 2048, double minHz = 55, double maxHz = 1500,
        double threshold = 0.15, double aperiodicityLimit = 0.3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 256);
        if (!(minHz > 0 && maxHz > minHz)) throw new ArgumentOutOfRangeException(nameof(minHz), "Need 0 < minHz < maxHz.");

        SampleRate = sampleRate;
        WindowSize = windowSize;
        Threshold = threshold;
        AperiodicityLimit = aperiodicityLimit;
        MinLag = Math.Max(2, (int)Math.Floor(sampleRate / maxHz));
        MaxLag = (int)Math.Ceiling(sampleRate / minHz);
        _difference = new float[MaxLag + 2];
        _cmnd = new float[MaxLag + 2];
    }

    public int SampleRate { get; }
    public int WindowSize { get; }
    public double Threshold { get; }
    public double AperiodicityLimit { get; }
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
        if (best < 0) best = FallbackLag(maxLag);
        if (best < 0) return PitchEstimate.None; // aperiodic (noise, chord mush, silence)

        var confidence = Math.Clamp(1 - _cmnd[best], 0, 1);

        // 4. Refine on the raw difference function, not on d'. Broadband noise adds a constant to d(τ); d' multiplies
        //    that by τ, which tilts the dip toward shorter lags and reads sharp at low SNR (measured: +34..+54 ¢ about
        //    10 dB above the noise floor). d itself only shifts, so its minimum stays put. Then parabolic interpolation.
        var lag = best;
        for (var tau = Math.Max(1, best - 2); tau <= Math.Min(maxLag, best + 2); tau++)
            if (_difference[tau] < _difference[lag]) lag = tau;

        var refined = (double)lag;
        if (lag > 1 && lag < maxLag)
        {
            double a = _difference[lag - 1], b = _difference[lag], c = _difference[lag + 1];
            var denominator = a - 2 * b + c;
            if (denominator > 0) refined = lag + 0.5 * (a - c) / denominator;
        }

        return new PitchEstimate(SampleRate / refined, confidence);
    }

    /// <summary>
    /// Global CMND minimum if it is periodic enough; then the smallest integer fraction of it (τ/2, τ/3, ...) whose dip is
    /// nearly as deep, so harmonic-rich notes don't read an octave low. −1 when aperiodic.
    /// </summary>
    private int FallbackLag(int maxLag)
    {
        var best = -1;
        var bestValue = float.MaxValue;
        for (var tau = MinLag; tau < maxLag; tau++)
        {
            if (_cmnd[tau] < bestValue)
            {
                bestValue = _cmnd[tau];
                best = tau;
            }
        }
        if (best < 0 || bestValue >= AperiodicityLimit) return -1;

        for (var divisor = Math.Max(2, best / MinLag); divisor >= 2; divisor--)
        {
            var centre = (int)Math.Round((double)best / divisor);
            if (centre < MinLag) continue;
            var local = LocalMinimum(Math.Max(MinLag, centre - 3), Math.Min(maxLag - 1, centre + 3));
            if (_cmnd[local] < bestValue + 0.1f) return local;
        }
        return best;
    }

    private int LocalMinimum(int from, int to)
    {
        var best = from;
        for (var tau = from + 1; tau <= to; tau++)
            if (_cmnd[tau] < _cmnd[best]) best = tau;
        return best;
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
