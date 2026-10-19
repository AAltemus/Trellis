namespace GuitarPractice.Audio;

/// <summary>
/// Overdub looper on a full-duplex stream (<see cref="IAudioDuplex"/>). The loop is <c>Bars × BeatsPerBar</c> beats at
/// <c>Bpm</c>, after one bar of count-in clicks. <see cref="ArmRecord"/> records the next whole pass into a new layer
/// (up to <see cref="MaxLayers"/>); layers play back together with the click. What you play reaches the input
/// <see cref="LatencySamples"/> after you heard the loop (output → ears → hands → input), so each input sample is filed
/// at the loop position that was playing that long ago: a note played on the click lands on the beat.
/// <see cref="Process"/> doesn't allocate, lock or block; buffers for new layers are allocated by <see cref="ArmRecord"/>
/// on the UI thread, and undo/clear are applied by the audio thread.
/// </summary>
public sealed class Looper
{
    public const int MaxLayers = 8;
    public const double MinBpm = 30;
    public const double MaxBpm = 300;

    private readonly float[] _accent;
    private readonly float[] _beat;
    private readonly float[]?[] _layers = new float[MaxLayers][];
    private readonly long _beatSamplesX1000; // beat length × 1000, so the grid doesn't drift on fractional lengths

    // UI → audio. Stop, undo and clear carry the order they were asked in (_commands), so one still pending when a
    // layer is armed (e.g. Clear while the stream was stopped, then Record) doesn't cancel that later arm.
    private int _startRequest;
    private long _commands;
    private long _stopAsked, _stopDone;
    private long _undoAsked, _undoDone;
    private long _clearAsked, _clearDone;
    private long _armedAt;
    private float[]? _armed;          // the next layer's buffer, when armed

    // Audio-thread state.
    private bool _running;
    private long _sample;              // output samples since Start (count-in included)
    private int _committed;            // finished layers
    private float[]? _recording;       // layer being written
    private long _recordFrom;          // loop-relative "heard" sample where the current recording starts
    private float[]? _next;            // the following pass's layer while the current one takes its last samples
    private long _nextFrom;
    private float[]? _click;
    private int _clickPos;

    // Audio → UI.
    private int _publishedLayers;
    private long _publishedSample;
    private int _publishedState;       // 0 stopped, 1 count-in, 2 playing
    private int _publishedRecording;   // 0 no, 1 armed, 2 recording

    public Looper(int sampleRate, double bpm, int beatsPerBar, int bars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        SampleRate = sampleRate;
        Bpm = Math.Clamp(double.IsFinite(bpm) ? bpm : 100, MinBpm, MaxBpm);
        BeatsPerBar = Math.Clamp(beatsPerBar, 1, 12);
        Bars = Math.Clamp(bars, 1, 16);
        _beatSamplesX1000 = (long)Math.Round(60.0 / Bpm * sampleRate * 1000);
        LoopLength = (int)(_beatSamplesX1000 * BeatsPerBar * Bars / 1000);
        CountInLength = BeatStart(BeatsPerBar);
        _accent = ClickSynth.Click(sampleRate, 1500, 1.0f);
        _beat = ClickSynth.Click(sampleRate, 1000, 0.85f);
    }

    public int SampleRate { get; }
    public double Bpm { get; }
    public int BeatsPerBar { get; }
    public int Bars { get; }

    /// <summary>Loop length in samples (a whole number of beats).</summary>
    public int LoopLength { get; }

    /// <summary>One bar of clicks before the loop starts.</summary>
    public long CountInLength { get; }

    /// <summary>Round-trip latency to compensate (samples); set from calibration, changeable any time.</summary>
    public int LatencySamples { get; set; }

    public bool ClickEnabled { get; set; } = true;
    public float ClickVolume { get; set; } = 0.8f;
    public float LoopVolume { get; set; } = 1f;

    // ---- State for the UI ----

    public int LayerCount => Volatile.Read(ref _publishedLayers);
    public bool IsRunning => Volatile.Read(ref _publishedState) != 0;
    public bool IsCountingIn => Volatile.Read(ref _publishedState) == 1;
    public bool IsArmed => Volatile.Read(ref _publishedRecording) == 1;
    public bool IsRecording => Volatile.Read(ref _publishedRecording) == 2;

    /// <summary>Where the loop is (0–1), or during the count-in how far through it (0–1).</summary>
    public double Position
    {
        get
        {
            var s = Volatile.Read(ref _publishedSample);
            return s < CountInLength ? (double)s / CountInLength : (double)((s - CountInLength) % LoopLength) / LoopLength;
        }
    }

    /// <summary>The beat being heard (0-based within the bar) and the bar (0-based; −1 in the count-in).</summary>
    public (int Bar, int Beat) BarBeat
    {
        get
        {
            var s = Volatile.Read(ref _publishedSample);
            if (s < CountInLength) return (-1, BeatIndex(s));
            var beat = BeatIndex((s - CountInLength) % LoopLength);
            return (beat / BeatsPerBar, beat % BeatsPerBar);
        }
    }

    // ---- Commands (UI thread) ----

    public void Start() => Volatile.Write(ref _startRequest, 1);

    public void Stop() => Volatile.Write(ref _stopAsked, Interlocked.Increment(ref _commands));

    /// <summary>Records the next whole pass into a new layer (false when all layers are used).</summary>
    public bool ArmRecord()
    {
        // Arming while a layer records is fine: it records the following pass (back-to-back overdubs).
        var used = Volatile.Read(ref _publishedLayers) + (IsRecording ? 1 : 0);
        if (used >= MaxLayers || Volatile.Read(ref _armed) is not null) return false;
        Volatile.Write(ref _armedAt, Interlocked.Increment(ref _commands));
        Volatile.Write(ref _armed, new float[LoopLength]);
        return true;
    }

    /// <summary>Drops the last layer (or the recording in progress / the armed one).</summary>
    public void Undo() => Volatile.Write(ref _undoAsked, Interlocked.Increment(ref _commands));

    public void Clear() => Volatile.Write(ref _clearAsked, Interlocked.Increment(ref _commands));

    /// <summary>Drops the armed layer only if it was armed before the command (asked at <paramref name="asked"/>).</summary>
    private void DropArmedBefore(long asked)
    {
        if (Volatile.Read(ref _armedAt) < asked) Volatile.Write(ref _armed, null);
    }

    /// <summary>All finished layers mixed (soft-limited), as one loop's worth of samples; null without layers.</summary>
    public float[]? MixDown()
    {
        var count = Volatile.Read(ref _publishedLayers);
        if (count == 0) return null;
        var mix = new float[LoopLength];
        for (var l = 0; l < count; l++)
            if (Volatile.Read(ref _layers[l]) is { } layer)
                for (var i = 0; i < mix.Length; i++) mix[i] += layer[i];
        for (var i = 0; i < mix.Length; i++) mix[i] = SoftLimit(mix[i]);
        return mix;
    }

    // ---- Audio thread ----

    /// <summary>The duplex callback: <paramref name="input"/> and <paramref name="output"/> are the same instant.</summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        HandleRequests();
        if (!_running)
        {
            output.Clear();
            Publish();
            return;
        }

        var latency = Math.Max(0, LatencySamples);
        var click = ClickEnabled ? ClickVolume : 0f;
        var loopGain = LoopVolume;
        for (var i = 0; i < output.Length; i++)
        {
            var s = _sample + i;
            var value = 0f;

            // Click on every beat (count-in always clicks).
            var beatSample = s < CountInLength ? s : (s - CountInLength) % LoopLength;
            if (IsOnBeat(beatSample))
            {
                var beat = BeatIndex(beatSample);
                _click = beat % BeatsPerBar == 0 ? _accent : _beat;
                _clickPos = 0;
            }
            if (_click is { } c)
            {
                value += c[_clickPos++] * (s < CountInLength ? Math.Max(click, 0.6f) : click);
                if (_clickPos >= c.Length) _click = null;
            }

            if (s >= CountInLength)
            {
                var p = (int)((s - CountInLength) % LoopLength);
                // A new pass begins: start recording if armed. The previous layer may still be taking its last
                // `latency` samples; then this one waits in _next and both are filled side by side.
                if (p == 0 && Volatile.Read(ref _armed) is { } armed && _committed + (_recording is null ? 0 : 1) < MaxLayers)
                {
                    Volatile.Write(ref _armed, null);
                    if (_recording is null)
                    {
                        _recording = armed;
                        _recordFrom = s - CountInLength;
                    }
                    else
                    {
                        _next = armed;
                        _nextFrom = s - CountInLength;
                    }
                }
                var mix = 0f;
                for (var l = 0; l < _committed; l++) mix += _layers[l]![p];
                value += mix * loopGain;
            }

            // File the input at the loop position that was playing `latency` samples ago.
            if (_recording is { } rec && i < input.Length)
            {
                var heard = s - CountInLength - latency - _recordFrom;
                if (heard >= 0 && heard < LoopLength) rec[heard] = input[i];
                else if (heard >= LoopLength) Commit();
            }
            if (_next is { } next && i < input.Length)
            {
                var heard = s - CountInLength - latency - _nextFrom;
                if (heard >= 0 && heard < LoopLength) next[heard] = input[i];
            }

            output[i] = SoftLimit(value);
        }
        _sample += output.Length;
        Publish();
    }

    private void Commit()
    {
        if (_recording is null) return;
        Volatile.Write(ref _layers[_committed], _recording);
        _committed++;
        _recording = _next;          // the next pass's layer (already being filled) carries on
        _recordFrom = _nextFrom;
        _next = null;
    }

    private void HandleRequests()
    {
        var clear = Volatile.Read(ref _clearAsked);
        if (clear != _clearDone)
        {
            _clearDone = clear;
            _recording = null;
            _next = null;
            DropArmedBefore(clear);
            for (var l = 0; l < MaxLayers; l++) Volatile.Write(ref _layers[l], null);
            _committed = 0;
        }
        var undo = Volatile.Read(ref _undoAsked);
        if (undo != _undoDone)
        {
            _undoDone = undo;
            if (_next is not null) _next = null;
            else if (_recording is not null) _recording = null;
            else if (Volatile.Read(ref _armed) is not null && Volatile.Read(ref _armedAt) < undo) Volatile.Write(ref _armed, null);
            else if (_committed > 0) Volatile.Write(ref _layers[--_committed], null);
        }
        var stop = Volatile.Read(ref _stopAsked);
        if (stop != _stopDone)
        {
            _stopDone = stop;
            _running = false;
            _recording = null;            // a pass cut short isn't kept
            _next = null;
            DropArmedBefore(stop);
            _click = null;
        }
        if (Interlocked.Exchange(ref _startRequest, 0) == 1)
        {
            _running = true;
            _sample = 0;
            _click = null;
        }
    }

    private void Publish()
    {
        Volatile.Write(ref _publishedLayers, _committed);
        Volatile.Write(ref _publishedSample, _sample);
        Volatile.Write(ref _publishedState, !_running ? 0 : _sample < CountInLength ? 1 : 2);
        Volatile.Write(ref _publishedRecording, _recording is not null ? 2 : Volatile.Read(ref _armed) is not null ? 1 : 0);
    }

    private long BeatStart(long beat) => beat * _beatSamplesX1000 / 1000;

    private int BeatIndex(long sample) => (int)(sample * 1000 / _beatSamplesX1000);

    private bool IsOnBeat(long sample) => BeatStart(BeatIndex(sample)) == sample;

    private static float SoftLimit(float x)
    {
        const float Knee = 0.8f;
        var m = MathF.Abs(x);
        if (m <= Knee) return x;
        var limited = Knee + (1 - Knee) * MathF.Tanh((m - Knee) / (1 - Knee));
        return x < 0 ? -limited : limited;
    }
}
