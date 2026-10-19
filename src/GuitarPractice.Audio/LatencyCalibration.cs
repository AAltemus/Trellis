namespace GuitarPractice.Audio;

/// <summary>Round-trip latency found by calibration; <see cref="LatencySamples"/> null when it couldn't be measured.</summary>
public sealed record CalibrationResult(int? LatencySamples, double? Milliseconds, int ClicksHeard, string Message);

/// <summary>
/// Measures the looper's round trip (output → speaker or cable → input) on a full-duplex stream: plays
/// <see cref="Clicks"/> clicks and records what comes back; <see cref="Analyze"/> finds each click's onset and takes
/// the median delay, if enough clicks agree. Hold the guitar's input (or a mic) where it hears the speakers, or loop a
/// cable from output to input. <see cref="Process"/> doesn't allocate.
/// </summary>
public sealed class LatencyProbe
{
    public const int Clicks = 8;
    private const double FirstClick = 0.3;
    private const double Spacing = 0.4;
    private const double Tail = 0.5;

    private readonly float[] _click;
    private readonly long[] _clickTimes = new long[Clicks];
    private readonly float[] _recorded;
    private long _sample;

    public LatencyProbe(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        SampleRate = sampleRate;
        _click = ClickSynth.Click(sampleRate, 2000, 0.9f, 0.012);
        for (var k = 0; k < Clicks; k++) _clickTimes[k] = (long)Math.Round((FirstClick + k * Spacing) * sampleRate);
        _recorded = new float[(int)Math.Round((FirstClick + Clicks * Spacing + Tail) * sampleRate)];
    }

    public int SampleRate { get; }

    public bool IsDone => Volatile.Read(ref _sample) >= _recorded.Length;

    public double Progress => Math.Min(1, (double)Volatile.Read(ref _sample) / _recorded.Length);

    /// <summary>The duplex callback.</summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        var start = _sample;
        for (var i = 0; i < output.Length; i++)
        {
            var s = start + i;
            var value = 0f;
            foreach (var t in _clickTimes)
                if (s >= t && s < t + _click.Length) value += _click[s - t];
            output[i] = value;
            if (s < _recorded.Length && i < input.Length) _recorded[s] = input[i];
        }
        Volatile.Write(ref _sample, start + output.Length);
    }

    /// <summary>Call once <see cref="IsDone"/>.</summary>
    public CalibrationResult Result() => Analyze(_recorded, _clickTimes, SampleRate, Onset(_click));

    /// <summary>Where a click itself first reaches 30 % of its peak (the analysis measures to that point).</summary>
    private static int Onset(float[] click)
    {
        var peak = click.Max(Math.Abs);
        var i = 0;
        while (i < click.Length && Math.Abs(click[i]) < peak * 0.3f) i++;
        return i;
    }

    /// <summary>
    /// Finds each click in <paramref name="recorded"/> (same clock as the clicks played at <paramref name="clickTimes"/>):
    /// within 350 ms after it, the first sample reaching 30 % of that window's peak, if the peak stands clear of the
    /// noise before the first click. The latency is the median delay when at least 5 clicks agree within 1.5 ms.
    /// </summary>
    /// <param name="clickOnset">How far into the click its own 30 % point is (subtracted).</param>
    public static CalibrationResult Analyze(float[] recorded, IReadOnlyList<long> clickTimes, int sampleRate, int clickOnset = 0)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(clickTimes);
        if (clickTimes.Count == 0) return new CalibrationResult(null, null, 0, "No clicks were played.");

        var noiseEnd = (int)Math.Min(recorded.Length, Math.Max(1, clickTimes[0] - sampleRate / 20));
        var noise = 0.0;
        for (var i = 0; i < noiseEnd; i++) noise = Math.Max(noise, Math.Abs(recorded[i]));
        var floor = Math.Max(0.005, noise * 4);

        var window = (int)(0.35 * sampleRate);
        var delays = new List<int>();
        foreach (var t in clickTimes)
        {
            var from = (int)t;
            var to = Math.Min(recorded.Length, from + window);
            if (from >= to) continue;
            var peak = 0f;
            for (var i = from; i < to; i++) peak = Math.Max(peak, Math.Abs(recorded[i]));
            if (peak < floor) continue;
            var onset = from;
            while (onset < to && Math.Abs(recorded[onset]) < peak * 0.3f) onset++;
            delays.Add(onset - from - clickOnset);
        }

        if (delays.Count < 5)
            return new CalibrationResult(null, null, delays.Count,
                $"Heard only {delays.Count} of {clickTimes.Count} clicks. Turn the speakers up and hold the guitar (or a mic) near them, or loop a cable from the output to the input.");

        delays.Sort();
        var median = delays[delays.Count / 2];
        var spread = (int)(0.0015 * sampleRate);
        var agreeing = delays.Count(d => Math.Abs(d - median) <= spread);
        if (agreeing < 5)
            return new CalibrationResult(null, null, delays.Count,
                "The clicks came back at different times (noise or echoes?). Try again somewhere quieter, or with a cable from output to input.");

        var ms = median * 1000.0 / sampleRate;
        return new CalibrationResult(median, ms, delays.Count, $"Round trip {ms:0.0} ms ({agreeing} of {clickTimes.Count} clicks agree).");
    }
}
