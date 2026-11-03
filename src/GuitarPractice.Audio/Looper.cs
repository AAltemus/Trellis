namespace GuitarPractice.Audio;

/// <summary>
/// Overdub looper on a full-duplex stream (<see cref="IAudioDuplex"/>). The loop is <c>Bars × BeatsPerBar</c> beats at
/// <c>Bpm</c>, after one bar of count-in clicks. <see cref="ArmRecord"/> records the next whole pass (or 2 or 4 passes:
/// a longer layer, e.g. a solo over a short chord loop) into a new layer (up to <see cref="MaxLayers"/>); layers play
/// back together with the click, each with its own gain. What you play reaches the input <see cref="LatencySamples"/>
/// after you heard the loop (output → ears → hands → input), so each input sample is filed at the loop position that was
/// playing that long ago: a note played on the click lands on the beat.
/// <see cref="Process"/> doesn't allocate, lock or block; buffers for new layers are allocated by <see cref="ArmRecord"/>
/// on the UI thread, and undo/clear are applied by the audio thread.
/// </summary>
public sealed class Looper
{
    public const int MaxLayers = 8;
    public const int MaxPasses = 4;
    public const double MinBpm = 30;
    public const double MaxBpm = 300;

    /// <summary>A layer is at most this long (seconds), so a long loop can't take gigabytes.</summary>
    public const double MaxLayerSeconds = 300;

    private readonly float[] _accent;
    private readonly float[] _beat;
    private readonly float[]?[] _layers = new float[MaxLayers][];
    private readonly long[] _layerStartPass = new long[MaxLayers]; // the pass a layer's first sample was recorded in
    private readonly float[] _layerGain = new float[MaxLayers];
    private readonly long _beatSamplesX1000; // beat length × 1000, so the grid doesn't drift on fractional lengths
    private readonly long _barLength;

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
    private long _passOrigin;          // the pass number of this run's first pass (keeps long layers in step)
    private int _committed;            // finished layers
    private float[]? _recording;       // layer being written
    private long _recordFrom;          // loop-relative "heard" sample where the current recording starts
    private float[]? _next;            // the following layer while the current one takes its last samples
    private long _nextFrom;
    private float[]? _click;
    private int _clickPos;
    private bool _countingDown;        // the bar before an armed layer starts recording

    // Audio → UI.
    private int _publishedLayers;
    private long _publishedSample;
    private int _publishedState;       // 0 stopped, 1 count-in, 2 playing
    private int _publishedRecording;   // 0 no, 1 armed, 2 recording, 3 armed and counting in to it
    private int _publishedRecordingPass;
    private int _publishedRecordingPasses;

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
        _barLength = CountInLength;
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

    /// <summary>The bar before an armed layer starts clicks at count-in volume, even with the click off or quiet.</summary>
    public bool CountInBeforeRecording { get; set; } = true;

    /// <summary>The most passes one layer can span at this loop length (1–<see cref="MaxPasses"/>).</summary>
    public int MaxPassesPerLayer => (int)Math.Clamp(MaxLayerSeconds * SampleRate / LoopLength, 1, MaxPasses);

    // ---- State for the UI ----

    public int LayerCount => Volatile.Read(ref _publishedLayers);
    public bool IsRunning => Volatile.Read(ref _publishedState) != 0;
    public bool IsCountingIn => Volatile.Read(ref _publishedState) == 1;
    public bool IsArmed => Volatile.Read(ref _publishedRecording) is 1 or 3;
    public bool IsRecording => Volatile.Read(ref _publishedRecording) == 2;

    /// <summary>The armed layer starts at the next bar 1 and this bar is its count-in.</summary>
    public bool IsCountingInToRecord => Volatile.Read(ref _publishedRecording) == 3;

    /// <summary>While recording: which pass of the layer (1-based) is being recorded.</summary>
    public int RecordingPass => Volatile.Read(ref _publishedRecordingPass);

    /// <summary>While recording: how many passes the layer being recorded spans.</summary>
    public int RecordingPasses => Volatile.Read(ref _publishedRecordingPasses);

    /// <summary>How many passes layer <paramref name="index"/> spans (0 if there's no such layer).</summary>
    public int LayerPasses(int index) =>
        index >= 0 && index < MaxLayers && Volatile.Read(ref _layers[index]) is { } layer ? layer.Length / LoopLength : 0;

    /// <summary>Layer <paramref name="index"/>'s gain (0 = muted, 1 = as recorded); applies at once.</summary>
    public void SetLayerGain(int index, float gain)
    {
        if (index < 0 || index >= MaxLayers) return;
        Volatile.Write(ref _layerGain[index], float.IsFinite(gain) ? Math.Clamp(gain, 0f, 2f) : 1f);
    }

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

    /// <summary>Records the next <paramref name="passes"/> whole passes into a new layer (false when all layers are used,
    /// one is already armed, or the layer would be longer than <see cref="MaxPassesPerLayer"/>).</summary>
    public bool ArmRecord(int passes = 1)
    {
        // Arming while a layer records is fine: it records the pass after that layer ends (back-to-back overdubs).
        var used = Volatile.Read(ref _publishedLayers) + (IsRecording ? 1 : 0);
        // 1, 2 or 4 passes: any two lengths then line up every 4 passes, and the mix-down is as long as the longest layer.
        if (used >= MaxLayers || Volatile.Read(ref _armed) is not null || passes is not (1 or 2 or 4) || passes > MaxPassesPerLayer)
            return false;
        Volatile.Write(ref _armedAt, Interlocked.Increment(ref _commands));
        Volatile.Write(ref _armed, new float[(long)LoopLength * passes]);
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

    /// <summary>
    /// All finished layers mixed at their gains (soft-limited), null without layers. As long as the longest layer (one
    /// loop when every layer is one pass), starting where ▶ Start starts: at the oldest long layer's first pass.
    /// </summary>
    public float[]? MixDown()
    {
        var count = Volatile.Read(ref _publishedLayers);
        if (count == 0) return null;
        var passes = 1;
        for (var l = 0; l < count; l++) passes = Math.Max(passes, LayerPasses(l));
        var origin = PassOrigin(count);
        var mix = new float[(long)LoopLength * passes];
        for (var l = 0; l < count; l++)
        {
            if (Volatile.Read(ref _layers[l]) is not { } layer) continue;
            var gain = Volatile.Read(ref _layerGain[l]);
            for (var pass = 0; pass < passes; pass++)
            {
                var from = LayerOffset(l, layer, origin + pass);
                for (var p = 0; p < LoopLength; p++) mix[(long)pass * LoopLength + p] += layer[from + p] * gain;
            }
        }
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
        var countInToRecord = CountInBeforeRecording;
        for (var i = 0; i < output.Length; i++)
        {
            var s = _sample + i;
            var value = 0f;
            var p = s >= CountInLength ? (int)((s - CountInLength) % LoopLength) : -1;
            var pass = s >= CountInLength ? _passOrigin + (s - CountInLength) / LoopLength : 0;

            // A new pass begins: start recording if armed. The previous layer may still be taking its last `latency`
            // samples; then this one waits in _next and both are filled side by side.
            if (p == 0 && Volatile.Read(ref _armed) is { } armed && StartsNow(s))
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

            // Click on every beat; the start's count-in always clicks, and so does the bar before an armed layer.
            _countingDown = p >= LoopLength - _barLength && Volatile.Read(ref _armed) is not null && StartsNow(s + LoopLength - p);
            var beatSample = s < CountInLength ? s : p;
            if (IsOnBeat(beatSample))
            {
                var beat = BeatIndex(beatSample);
                _click = beat % BeatsPerBar == 0 ? _accent : _beat;
                _clickPos = 0;
            }
            if (_click is { } c)
            {
                var loud = s < CountInLength || (countInToRecord && _countingDown);
                value += c[_clickPos++] * (loud ? Math.Max(click, 0.6f) : click);
                if (_clickPos >= c.Length) _click = null;
            }

            if (p >= 0)
            {
                var mix = 0f;
                for (var l = 0; l < _committed; l++)
                {
                    var layer = _layers[l]!;
                    mix += layer[LayerOffset(l, layer, pass) + p] * _layerGain[l];
                }
                value += mix * loopGain;
            }

            // File the input at the loop position that was playing `latency` samples ago.
            if (_recording is { } rec && i < input.Length)
            {
                var heard = s - CountInLength - latency - _recordFrom;
                if (heard >= 0 && heard < rec.Length) rec[heard] = input[i];
                else if (heard >= rec.Length) Commit();
            }
            if (_next is { } next && i < input.Length)
            {
                var heard = s - CountInLength - latency - _nextFrom;
                if (heard >= 0 && heard < next.Length) next[heard] = input[i];
            }

            output[i] = SoftLimit(value);
        }
        _sample += output.Length;
        Publish();
    }

    /// <summary>An armed layer may start at loop sample <paramref name="s"/> (a pass boundary): a layer slot is free and
    /// any layer being recorded has played all its passes (it may still be taking its last latency samples).</summary>
    private bool StartsNow(long s) =>
        _next is null
        && _committed + (_recording is null ? 0 : 1) < MaxLayers
        && (_recording is not { } rec || s - CountInLength - _recordFrom >= rec.Length);

    /// <summary>Where in <paramref name="layer"/> the given absolute pass plays from.</summary>
    private long LayerOffset(int index, float[] layer, long pass)
    {
        var passes = layer.Length / LoopLength;
        if (passes == 1) return 0;
        var phase = (pass - _layerStartPass[index]) % passes;
        return (phase < 0 ? phase + passes : phase) * LoopLength;
    }

    /// <summary>The pass ▶ Start begins on: the oldest long layer's first pass, so it plays from its start.</summary>
    private long PassOrigin(int committed)
    {
        for (var l = 0; l < committed; l++)
            if (Volatile.Read(ref _layers[l]) is { } layer && layer.Length > LoopLength) return _layerStartPass[l];
        return 0;
    }

    private void Commit()
    {
        if (_recording is null) return;
        _layerStartPass[_committed] = _passOrigin + _recordFrom / LoopLength;
        Volatile.Write(ref _layerGain[_committed], 1f);
        Volatile.Write(ref _layers[_committed], _recording);
        _committed++;
        _recording = _next;          // the next layer (already being filled) carries on
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
            _passOrigin = PassOrigin(_committed);
            _click = null;
        }
    }

    private void Publish()
    {
        Volatile.Write(ref _publishedLayers, _committed);
        Volatile.Write(ref _publishedSample, _sample);
        Volatile.Write(ref _publishedState, !_running ? 0 : _sample < CountInLength ? 1 : 2);
        var armed = Volatile.Read(ref _armed) is not null;
        Volatile.Write(ref _publishedRecording, _recording is not null ? 2 : armed && _countingDown ? 3 : armed ? 1 : 0);
        Volatile.Write(ref _publishedRecordingPass, _recording is { } rec
            ? (int)Math.Clamp((_sample - CountInLength - _recordFrom) / LoopLength + 1, 1, rec.Length / LoopLength)
            : 0);
        Volatile.Write(ref _publishedRecordingPasses, _recording is { } r ? r.Length / LoopLength : 0);
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
