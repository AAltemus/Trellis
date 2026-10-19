namespace GuitarPractice.Audio;

/// <summary>
/// Slows a take down (or speeds it up) without changing its pitch: WSOLA (waveform-similarity overlap-add). 40 ms Hann
/// frames are laid down every 20 ms of output; each is read from the input near where the speed puts it (±10 ms),
/// at the offset whose start best matches how the previous frame would have continued, so waveforms join in phase
/// (no warble, no clicks). The search runs on a 4× decimated copy, then is refined at full rate. Offline: the result is
/// a new take, about 1/speed as long (the Recorder stretches in the background and plays that).
/// </summary>
public static class TimeStretch
{
    public const double MinSpeed = 0.5;
    public const double MaxSpeed = 1.0;

    private const double FrameSeconds = 0.040;
    private const double ToleranceSeconds = 0.010;
    private const int Decimation = 4;

    /// <summary>
    /// The take at <paramref name="speed"/> (0.5–1; 1 returns the same take), or null if <paramref name="cancel"/> was
    /// cancelled (checked every 64 frames; no exception: cancelling is routine). <paramref name="progress"/> gets 0–1 now and then.
    /// </summary>
    public static AudioTake? Stretch(AudioTake take, double speed, Action<double>? progress = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(take);
        if (!double.IsFinite(speed)) throw new ArgumentOutOfRangeException(nameof(speed));
        speed = Math.Clamp(speed, MinSpeed, MaxSpeed);
        if (speed >= MaxSpeed - 1e-9) return take;

        var input = take.Samples;
        var rate = take.SampleRate;
        var n = Math.Max(64, (int)Math.Round(FrameSeconds * rate) & ~1); // even
        var hop = n / 2;                                                  // synthesis hop: 50 % overlap
        var tolerance = Math.Max(Decimation, (int)Math.Round(ToleranceSeconds * rate));
        var outLength = (int)Math.Round(input.Length / speed);
        if (input.Length < n * 2) return new AudioTake(Resample(input, outLength), rate); // too short to stretch: plain

        var window = new float[n];
        for (var i = 0; i < n; i++) window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n)); // periodic Hann: sums to 1 at 50 %

        var coarse = Decimate(input);
        var output = new float[outLength + n];
        var previous = 0;
        var frames = (outLength - n) / hop + 1;
        var k = 0;
        for (; k < frames; k++)
        {
            if ((k & 63) == 0)
            {
                if (cancel.IsCancellationRequested) return null;
                progress?.Invoke((double)k / frames);
            }
            var nominal = (int)Math.Round(k * hop * speed);
            if (nominal + n > input.Length) break;
            var start = k == 0 ? 0 : BestStart(input, coarse, nominal, previous + hop, hop, tolerance, n);

            var outPos = k * hop;
            if (k == 0)
                for (var i = 0; i < hop; i++) output[i] = input[i];                   // nothing overlaps the first half
            else
                for (var i = 0; i < hop; i++) output[outPos + i] += input[start + i] * window[i];
            for (var i = hop; i < n; i++) output[outPos + i] = input[start + i] * window[i];
            previous = start;
        }

        // The last frame's second half has nothing after it: take it unwindowed, then the rest of the input plainly.
        var lastOut = (k - 1) * hop + hop;
        var lastIn = previous + hop;
        for (var i = 0; lastOut + i < outLength; i++)
            output[lastOut + i] = lastIn + i < input.Length ? input[lastIn + i] : 0f;

        progress?.Invoke(1);
        return new AudioTake(output[..outLength], rate);
    }

    /// <summary>
    /// The frame start within ±<paramref name="tolerance"/> of <paramref name="nominal"/> whose first half best matches
    /// the natural continuation of the previous frame (input from <paramref name="continuation"/>).
    /// </summary>
    private static int BestStart(float[] input, float[] coarse, int nominal, int continuation, int length, int tolerance, int n)
    {
        var lo = Math.Max(0, nominal - tolerance);
        var hi = Math.Min(input.Length - n, nominal + tolerance);
        if (continuation + length > input.Length) continuation = Math.Max(0, input.Length - length);
        if (hi <= lo) return Math.Clamp(nominal, 0, Math.Max(0, input.Length - n));

        // Coarse search on the decimated signal.
        var cLength = length / Decimation;
        var cTemplate = continuation / Decimation;
        var best = lo;
        var bestScore = double.NegativeInfinity;
        for (var c = lo / Decimation; c <= hi / Decimation; c++)
        {
            if (c + cLength > coarse.Length || cTemplate + cLength > coarse.Length) break;
            var score = Dot(coarse, c, coarse, cTemplate, cLength);
            if (score > bestScore)
            {
                bestScore = score;
                best = c * Decimation;
            }
        }

        // Refine at full rate around the coarse pick.
        var refined = best;
        bestScore = double.NegativeInfinity;
        for (var p = Math.Max(lo, best - Decimation); p <= Math.Min(hi, best + Decimation); p++)
        {
            var score = Dot(input, p, input, continuation, length);
            if (score > bestScore)
            {
                bestScore = score;
                refined = p;
            }
        }
        return refined;
    }

    private static double Dot(float[] a, int aStart, float[] b, int bStart, int length)
    {
        var sum = 0.0;
        for (var i = 0; i < length; i++) sum += a[aStart + i] * b[bStart + i];
        return sum;
    }

    private static float[] Decimate(float[] input)
    {
        var result = new float[input.Length / Decimation];
        for (var i = 0; i < result.Length; i++)
        {
            var sum = 0f;
            for (var j = 0; j < Decimation; j++) sum += input[i * Decimation + j];
            result[i] = sum / Decimation;
        }
        return result;
    }

    private static float[] Resample(float[] input, int length)
    {
        var result = new float[length];
        if (input.Length == 0) return result;
        for (var i = 0; i < length; i++)
        {
            var x = (double)i * (input.Length - 1) / Math.Max(1, length - 1);
            var j = (int)x;
            result[i] = j + 1 < input.Length ? input[j] + (input[j + 1] - input[j]) * (float)(x - j) : input[^1];
        }
        return result;
    }
}
