using System.Diagnostics;

namespace GuitarPractice.Audio;

/// <summary>
/// What the tuner shows. <see cref="TargetMidi"/> is the nearest note (chromatic) or the closest string of the tuning;
/// <see cref="Cents"/> is relative to it and is not limited to ±50 in tuning mode (a string tuned a tone low reads −200).
/// </summary>
public sealed record TunerReading(double Hz, int TargetMidi, double Cents, double Confidence, int? StringIndex);

/// <summary>Latest analysis result, published by the analysis thread as one immutable object.</summary>
public sealed record TunerState(TunerReading? Reading, double LevelDb);

/// <summary>
/// Turns per-frame pitch estimates into a steady display: median of the last 5 frequencies (kills single-frame
/// glitches), note hysteresis (a new target must win 3 frames in a row), EMA on cents, and a short hold so the
/// reading doesn't blink out between frames of a decaying note. Pure; one instance per analysis thread.
/// </summary>
public sealed class TunerSmoother
{
    public const int MedianFrames = 5;
    public const int StableFrames = 3;
    public const int HoldFrames = 8;          // ~170 ms at 47 frames/s
    private const double CentsSmoothing = 0.35;

    private readonly double[] _recent = new double[MedianFrames];
    private int _recentCount;
    private int _recentNext;
    private int? _shown;
    private int _candidate;
    private int _candidateFrames;
    private double _cents;
    private int _misses;
    private int[]? _strings;
    private double _a4 = 440;

    /// <summary>Reference pitch for A4 (Hz). Thread-safe to set.</summary>
    public double A4
    {
        get => Volatile.Read(ref _a4);
        set => Volatile.Write(ref _a4, Math.Clamp(value, 400, 480));
    }

    /// <summary>MIDI notes of the strings (any order); null = chromatic. Thread-safe to set.</summary>
    public IReadOnlyList<int>? Strings
    {
        get => Volatile.Read(ref _strings);
        set => Volatile.Write(ref _strings, value?.ToArray());
    }

    public TunerReading? Current { get; private set; }

    public void Reset()
    {
        _recentCount = 0;
        _recentNext = 0;
        _shown = null;
        _candidateFrames = 0;
        _misses = 0;
        Current = null;
    }

    public TunerReading? Add(PitchEstimate estimate)
    {
        if (!estimate.IsVoiced)
        {
            if (++_misses >= HoldFrames) Reset();
            return Current;
        }
        _misses = 0;

        _recent[_recentNext] = estimate.Hz;
        _recentNext = (_recentNext + 1) % MedianFrames;
        if (_recentCount < MedianFrames) _recentCount++;
        var hz = Median(_recent.AsSpan(0, _recentCount));

        var a4 = A4;
        var midi = Pitch.MidiFromFrequency(hz, a4);
        var strings = Volatile.Read(ref _strings);
        var stringIndex = strings is { Length: > 0 } ? ClosestString(midi, strings) : -1;
        var target = stringIndex >= 0 ? strings![stringIndex] : (int)Math.Round(midi);

        var switched = false;
        if (_shown != target)
        {
            if (target == _candidate) _candidateFrames++;
            else
            {
                _candidate = target;
                _candidateFrames = 1;
            }
            if (_candidateFrames >= StableFrames)
            {
                _shown = target;
                switched = true;
            }
        }
        else _candidateFrames = 0;

        if (_shown is not int shown) return Current = null;

        var raw = (midi - shown) * 100;
        _cents = switched || Current is null ? raw : _cents + CentsSmoothing * (raw - _cents);
        int? shownString = strings is { Length: > 0 } ? Array.IndexOf(strings, shown) is var i and >= 0 ? i : null : null;
        return Current = new TunerReading(hz, shown, _cents, estimate.Confidence, shownString);
    }

    /// <summary>Index of the string whose note is closest (in semitones) to <paramref name="midi"/>.</summary>
    public static int ClosestString(double midi, IReadOnlyList<int> strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < strings.Count; i++)
        {
            var distance = Math.Abs(midi - strings[i]);
            if (distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }
        return best;
    }

    private static double Median(ReadOnlySpan<double> values)
    {
        Span<double> sorted = stackalloc double[MedianFrames];
        values.CopyTo(sorted);
        sorted = sorted[..values.Length];
        sorted.Sort();
        return values.Length % 2 == 1
            ? sorted[values.Length / 2]
            : (sorted[values.Length / 2 - 1] + sorted[values.Length / 2]) / 2;
    }
}

/// <summary>RBJ-cookbook biquad high-pass (Direct Form I). Removes rumble and DC before pitch detection.</summary>
internal sealed class HighPassFilter
{
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _x1, _x2, _y1, _y2;

    public HighPassFilter(int sampleRate, double cutoffHz, double q = 0.7071)
    {
        var w0 = 2 * Math.PI * cutoffHz / sampleRate;
        var cos = Math.Cos(w0);
        var alpha = Math.Sin(w0) / (2 * q);
        var a0 = 1 + alpha;
        _b0 = (1 + cos) / 2 / a0;
        _b1 = -(1 + cos) / a0;
        _b2 = (1 + cos) / 2 / a0;
        _a1 = -2 * cos / a0;
        _a2 = (1 - alpha) / a0;
    }

    public float Process(float x)
    {
        var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1;
        _x1 = x;
        _y2 = _y1;
        _y1 = y;
        return (float)y;
    }
}

/// <summary>
/// Tuner pipeline: capture callback (high-pass 40 Hz → lock-free ring, nothing else on the audio thread) → analysis
/// thread every 1024 new samples (~43–47 frames/s): latest window → RMS gate → YIN → <see cref="TunerSmoother"/> →
/// <see cref="State"/>. The UI polls <see cref="State"/>.
/// </summary>
public sealed class TunerEngine : IDisposable
{
    public const int Hop = 1024;
    private const int RingSize = 1 << 15; // 32768 samples (~0.7 s); the analysis only ever reads the newest window

    private readonly IAudioInput _input;
    private readonly bool _backgroundAnalysis;
    private readonly float[] _ring = new float[RingSize];
    private readonly AudioCaptureCallback _captureCallback;
    private float[] _window = [];
    private HighPassFilter? _highPass;
    private YinPitchDetector? _detector;
    private long _written;      // samples written by the capture thread
    private long _analyzedAt;   // _written at the last analysis
    private Thread? _thread;
    private volatile bool _stopping;
    private TunerState _state = new(null, double.NegativeInfinity);

    /// <param name="backgroundAnalysis">False for tests: call <see cref="ProcessPending"/> yourself.</param>
    public TunerEngine(IAudioInput input, bool backgroundAnalysis = true)
    {
        ArgumentNullException.ThrowIfNull(input);
        _input = input;
        _backgroundAnalysis = backgroundAnalysis;
        _captureCallback = OnCapture;
    }

    public TunerSmoother Smoother { get; } = new();

    /// <summary>RMS below this (dBFS) counts as silence. −54 dBFS suits a DI'd guitar on a quiet interface.</summary>
    public double GateDb { get; set; } = -54;

    public TunerState State => Volatile.Read(ref _state);

    public bool IsRunning => _input.IsRunning;

    public int SampleRate { get; private set; }

    public void Start(string? deviceId = null, int channel = 0)
    {
        Stop();
        SampleRate = _input.GetPreferredSampleRate(deviceId);
        _detector = new YinPitchDetector(SampleRate);
        _window = new float[_detector.RequiredSamples];
        _highPass = new HighPassFilter(SampleRate, 40);
        Volatile.Write(ref _written, 0);
        _analyzedAt = 0;
        Smoother.Reset();
        Volatile.Write(ref _state, new TunerState(null, double.NegativeInfinity));

        _input.Start(new AudioStreamOptions(deviceId, SampleRate, 512, channel), _captureCallback);

        if (_backgroundAnalysis)
        {
            _stopping = false;
            _thread = new Thread(AnalysisLoop) { IsBackground = true, Name = "Tuner analysis", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }
    }

    public void Stop()
    {
        _stopping = true;
        _thread?.Join(TimeSpan.FromSeconds(1));
        _thread = null;
        _input.Stop();
    }

    public void Dispose() => Stop();

    /// <summary>Analyzes once if a hop's worth of new audio has arrived. Returns true when it did.</summary>
    public bool ProcessPending()
    {
        var detector = _detector;
        if (detector is null) return false;
        var written = Volatile.Read(ref _written);
        if (written - _analyzedAt < Hop || written < _window.Length) return false;
        _analyzedAt = written;

        // Copy the newest window out of the ring (the writer is ~0.6 s away from wrapping onto it).
        var start = written - _window.Length;
        for (var i = 0; i < _window.Length; i++) _window[i] = _ring[(start + i) & (RingSize - 1)];

        double sumSquares = 0;
        foreach (var v in _window) sumSquares += v * v;
        var rms = Math.Sqrt(sumSquares / _window.Length);
        var levelDb = rms > 0 ? 20 * Math.Log10(rms) : double.NegativeInfinity;

        var estimate = levelDb < GateDb ? PitchEstimate.None : detector.Detect(_window);
        var reading = Smoother.Add(estimate);
        Volatile.Write(ref _state, new TunerState(reading, levelDb));
        return true;
    }

    private void OnCapture(ReadOnlySpan<float> samples)
    {
        var highPass = _highPass;
        if (highPass is null) return;
        var written = _written;
        foreach (var x in samples)
            _ring[written++ & (RingSize - 1)] = highPass.Process(x);
        Volatile.Write(ref _written, written);
    }

    private void AnalysisLoop()
    {
        while (!_stopping)
        {
            try
            {
                if (!ProcessPending()) Thread.Sleep(3);
            }
#pragma warning disable CA1031 // Keep the tuner alive; a bad frame is just skipped.
            catch (Exception ex)
            {
                Trace.TraceError($"Tuner analysis failed: {ex.Message}");
                Thread.Sleep(50);
            }
#pragma warning restore CA1031
        }
    }
}
