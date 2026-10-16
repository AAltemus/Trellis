using System.Diagnostics;

namespace GuitarPractice.Audio;

/// <summary>Clicks per beat.</summary>
public enum Subdivision
{
    Quarters = 1,
    Eighths = 2,
    Triplets = 3,
    Sixteenths = 4,
}

public enum ClickKind
{
    /// <summary>Beat 1 of the bar (when accents are on).</summary>
    Accent,
    Beat,
    Subdivision,
}

/// <summary>
/// Immutable; the UI publishes a new instance and the audio thread picks it up at the next click.
/// Gap training: play <see cref="GapPlayBars"/> bars, then <see cref="GapMuteBars"/> silent bars (timing continues).
/// Tempo trainer: +<see cref="TrainerStepBpm"/> every <see cref="TrainerEveryBars"/> bars, never past
/// <see cref="TrainerTargetBpm"/>; <see cref="Bpm"/> is the starting tempo. Both count whole bars from Start (or from
/// the bar after their own settings, or Bpm, last changed).
/// </summary>
public sealed record MetronomeSettings(
    double Bpm = 120,
    int BeatsPerBar = 4,
    Subdivision Subdivision = Subdivision.Quarters,
    bool AccentFirstBeat = true,
    float Volume = 0.8f,
    bool GapEnabled = false,
    int GapPlayBars = 3,
    int GapMuteBars = 1,
    bool TrainerEnabled = false,
    double TrainerStepBpm = 2,
    int TrainerEveryBars = 4,
    double TrainerTargetBpm = 160)
{
    public const double MinBpm = 20;
    public const double MaxBpm = 400;
    public const int MaxBeatsPerBar = 16;
    public const int MaxGapBars = 16;
    public const int MaxTrainerEveryBars = 64;
    public const double MaxTrainerStepBpm = 20;

    public int TicksPerBeat => (int)Subdivision;

    public MetronomeSettings Normalized() => this with
    {
        Bpm = double.IsFinite(Bpm) ? Math.Clamp(Bpm, MinBpm, MaxBpm) : 120,
        BeatsPerBar = Math.Clamp(BeatsPerBar, 1, MaxBeatsPerBar),
        Subdivision = Enum.IsDefined(Subdivision) ? Subdivision : Subdivision.Quarters,
        Volume = float.IsFinite(Volume) ? Math.Clamp(Volume, 0f, 1f) : 0.8f,
        GapPlayBars = Math.Clamp(GapPlayBars, 1, MaxGapBars),
        GapMuteBars = Math.Clamp(GapMuteBars, 1, MaxGapBars),
        TrainerStepBpm = double.IsFinite(TrainerStepBpm) ? Math.Clamp(TrainerStepBpm, 0.5, MaxTrainerStepBpm) : 2,
        TrainerEveryBars = Math.Clamp(TrainerEveryBars, 1, MaxTrainerEveryBars),
        TrainerTargetBpm = double.IsFinite(TrainerTargetBpm) ? Math.Clamp(TrainerTargetBpm, MinBpm, MaxBpm) : 160,
    };
}

/// <summary>
/// One click, reported to the UI. <see cref="SamplePosition"/> counts samples since Start; <see cref="Timestamp"/> is
/// the <see cref="Stopwatch"/> time at which that sample was handed to the audio device. Add the output latency to
/// get when it is heard. <see cref="Bpm"/> is the tempo in effect (the tempo trainer changes it);
/// <see cref="Muted"/> is true in a gap-training silent bar (no sound, timing continues).
/// </summary>
public readonly record struct MetronomeTick(
    long SamplePosition, long Timestamp, long Bar, int Beat, int SubTick, ClickKind Kind, double Bpm = 0, bool Muted = false);

/// <summary>
/// Sample-accurate metronome. Timing comes only from counting samples in <see cref="Render"/> (called on the audio
/// thread), never from UI timers. Click k of a tempo segment is due at segmentStart + k·period (fractional samples,
/// computed by multiplication, not by summing periods), so there is no drift at tempos whose period isn't a whole
/// number of samples. Settings changes take effect at the next click.
/// <see cref="Render"/> doesn't allocate, lock or block.
/// </summary>
public sealed class MetronomeEngine
{
    private readonly float[] _accentClick;
    private readonly float[] _beatClick;
    private readonly float[] _subdivisionClick;
    private readonly SpscRing<MetronomeTick> _ticks = new(256);

    private MetronomeSettings _settings;
    private int _startRequested;
    private volatile bool _running;

    // Audio-thread state.
    private const double Epsilon = 1e-6; // samples; absorbs floating-point rounding on exact grid points

    private long _sample;
    private double _nextTick;
    private double _period;       // samples per click in the current tempo segment
    private double _segmentStart; // sample position of the segment's first click
    private long _segmentIndex;   // clicks since the segment started
    private long _bar;
    private int _beat;
    private int _subTick;
    private float[]? _click;
    private int _clickPos;
    private float _clickGain;

    // Gap training / tempo trainer state (audio thread). A config change restarts the count at the next bar.
    private GapKey _gapKey;
    private long _gapBar;          // bars since the gap count (re)started; -1 = current partial bar plays
    private bool _mutedBar;
    private TrainerKey _trainerKey;
    private long _rampBars;        // whole bars since the ramp (re)started; -1 = current partial bar
    private double _effectiveBpm;

    private readonly record struct GapKey(bool Enabled, int Play, int Mute);

    private readonly record struct TrainerKey(double Bpm, bool Enabled, double Step, int Every, double Target);

    public MetronomeEngine(int sampleRate, MetronomeSettings? settings = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        SampleRate = sampleRate;
        _settings = (settings ?? new MetronomeSettings()).Normalized();
        _accentClick = ClickSynth.Click(sampleRate, 1500, 1.0f);
        _beatClick = ClickSynth.Click(sampleRate, 1000, 0.75f);
        _subdivisionClick = ClickSynth.Click(sampleRate, 800, 0.4f);
    }

    public int SampleRate { get; }

    public bool IsRunning => _running;

    public MetronomeSettings Settings
    {
        get => Volatile.Read(ref _settings);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _settings, value.Normalized());
        }
    }

    /// <summary>Starts (or restarts) from beat 1; the first click lands on the first sample of the next buffer.</summary>
    public void Start()
    {
        // Request the reset before flagging running, so the audio thread never plays stale state.
        Interlocked.Exchange(ref _startRequested, 1);
        _running = true;
    }

    public void Stop() => _running = false;

    /// <summary>Clicks rendered so far, for the UI. Returns false when none are waiting.</summary>
    public bool TryDequeueTick(out MetronomeTick tick) => _ticks.TryDequeue(out tick);

    /// <summary>Audio thread: fills <paramref name="output"/> (mono). Any buffer size.</summary>
    public void Render(Span<float> output)
    {
        if (Interlocked.Exchange(ref _startRequested, 0) == 1)
        {
            _sample = 0;
            _nextTick = 0;
            _period = 0;
            _bar = 0;
            _beat = 0;
            _subTick = 0;
            _click = null;
            _gapKey = default;
            _trainerKey = default;
        }

        if (!_running)
        {
            output.Clear();
            return;
        }

        var bufferTimestamp = Stopwatch.GetTimestamp();
        for (var i = 0; i < output.Length; i++)
        {
            while (_sample + Epsilon >= _nextTick) Fire(i, bufferTimestamp);

            var value = 0f;
            if (_click is { } click)
            {
                value = click[_clickPos++] * _clickGain;
                if (_clickPos == click.Length) _click = null;
            }
            output[i] = value;
            _sample++;
        }
    }

    private void Fire(int offsetInBuffer, long bufferTimestamp)
    {
        var s = Volatile.Read(ref _settings);
        var ticksPerBeat = s.TicksPerBeat;

        // Settings may have shrunk the bar or the subdivision since the last click.
        if (_subTick >= ticksPerBeat) { _subTick = 0; _beat++; }
        if (_beat >= s.BeatsPerBar) { _beat = 0; _bar++; }
        var barStart = _subTick == 0 && _beat == 0;
        UpdateGapAndTrainer(s, barStart);

        var kind = _subTick != 0 ? ClickKind.Subdivision
            : _beat == 0 && s.AccentFirstBeat ? ClickKind.Accent
            : ClickKind.Beat;

        _click = _mutedBar ? null : kind switch
        {
            ClickKind.Accent => _accentClick,
            ClickKind.Beat => _beatClick,
            _ => _subdivisionClick,
        };
        _clickPos = 0;
        _clickGain = s.Volume;

        var timestamp = bufferTimestamp + offsetInBuffer * Stopwatch.Frequency / SampleRate;
        _ticks.TryEnqueue(new MetronomeTick(_sample, timestamp, _bar, _beat, _subTick, kind, _effectiveBpm, _mutedBar)); // full → UI is behind; drop

        var period = SampleRate * 60.0 / (_effectiveBpm * ticksPerBeat);
        if (period != _period)
        {
            // New tempo segment, anchored on this click's scheduled position.
            _period = period;
            _segmentStart = _nextTick;
            _segmentIndex = 0;
        }
        _nextTick = _segmentStart + ++_segmentIndex * _period;

        if (++_subTick >= ticksPerBeat)
        {
            _subTick = 0;
            if (++_beat >= s.BeatsPerBar)
            {
                _beat = 0;
                _bar++;
            }
        }
    }

    /// <summary>Counts whole bars for gap training and the tempo trainer; sets _mutedBar and _effectiveBpm.</summary>
    private void UpdateGapAndTrainer(MetronomeSettings s, bool barStart)
    {
        var gapKey = new GapKey(s.GapEnabled, s.GapPlayBars, s.GapMuteBars);
        if (gapKey != _gapKey)
        {
            _gapKey = gapKey;
            _gapBar = -1;
            _mutedBar = false;
        }
        if (barStart) _gapBar++;
        _mutedBar = s.GapEnabled && _gapBar >= 0 && _gapBar % (s.GapPlayBars + s.GapMuteBars) >= s.GapPlayBars;

        var trainerKey = new TrainerKey(s.Bpm, s.TrainerEnabled, s.TrainerStepBpm, s.TrainerEveryBars, s.TrainerTargetBpm);
        if (trainerKey != _trainerKey)
        {
            _trainerKey = trainerKey;
            _rampBars = -1;
        }
        if (barStart) _rampBars++;

        var steps = s.TrainerEnabled && _rampBars > 0 ? _rampBars / s.TrainerEveryBars : 0;
        var headroom = Math.Max(0, s.TrainerTargetBpm - s.Bpm);
        _effectiveBpm = s.Bpm + Math.Min(steps * s.TrainerStepBpm, headroom);
    }
}

/// <summary>Short decaying-sine clicks, pre-rendered once so the audio thread only copies samples.</summary>
internal static class ClickSynth
{
    public static float[] Click(int sampleRate, double hz, float peak, double seconds = 0.015)
    {
        var n = (int)(sampleRate * seconds);
        var attack = Math.Max(1, (int)(0.0005 * sampleRate)); // 0.5 ms: crisp, but no DC step
        var release = Math.Max(1, (int)(0.002 * sampleRate)); // last 2 ms fade to exactly zero
        var data = new float[n];
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / sampleRate;
            var envelope = Math.Exp(-t / 0.004);
            if (i < attack) envelope *= (double)i / attack;
            if (i >= n - release) envelope *= (double)(n - 1 - i) / release;
            data[i] = (float)(peak * envelope * Math.Sin(2 * Math.PI * hz * t));
        }
        return data;
    }
}

/// <summary>
/// Lock-free single-producer/single-consumer ring (audio thread → UI thread). Fixed capacity, no allocation after
/// construction; TryEnqueue fails instead of overwriting when full.
/// </summary>
internal sealed class SpscRing<T>(int capacity) where T : struct
{
    private readonly T[] _items = new T[capacity];
    private long _head; // next to read (consumer)
    private long _tail; // next to write (producer)

    public bool TryEnqueue(in T item)
    {
        var tail = _tail;
        if (tail - Volatile.Read(ref _head) >= _items.Length) return false;
        _items[tail % _items.Length] = item;
        Volatile.Write(ref _tail, tail + 1);
        return true;
    }

    public bool TryDequeue(out T item)
    {
        var head = _head;
        if (head >= Volatile.Read(ref _tail))
        {
            item = default;
            return false;
        }
        item = _items[head % _items.Length];
        Volatile.Write(ref _head, head + 1);
        return true;
    }
}
