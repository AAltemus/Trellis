using System.Diagnostics;

namespace GuitarPractice.Audio;

/// <summary>Clicks per beat.</summary>
public enum Subdivision
{
    Quarters = 1,
    Eighths = 2,
    Triplets = 3,
    Sixteenths = 4,
    Quintuplets = 5,
    Sextuplets = 6,
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
/// Sound: the beep click, or the drum pattern <see cref="Drums"/> (null = <see cref="DrumPattern.BasicRock"/>); in drum mode
/// the accent setting is not used (the pattern is the accent).
/// </summary>
public sealed record MetronomeSettings(
    double Bpm = 120,
    int BeatsPerBar = 4,
    Subdivision Subdivision = Subdivision.Quarters,
    bool AccentFirstBeat = true,
    float Volume = 1.0f,
    bool GapEnabled = false,
    int GapPlayBars = 3,
    int GapMuteBars = 1,
    bool TrainerEnabled = false,
    double TrainerStepBpm = 2,
    int TrainerEveryBars = 4,
    double TrainerTargetBpm = 160,
    MetronomeSound Sound = MetronomeSound.Click,
    DrumPattern? Drums = null)
{
    public const double MinBpm = 20;
    public const double MaxBpm = 400;
    public const int MaxBeatsPerBar = 16;

    /// <summary>Volume is linear up to 1; above that (up to 2 = 200 %) a soft limiter makes clicks louder without clipping.</summary>
    public const float MaxVolume = 2f;
    public const int MaxGapBars = 16;
    public const int MaxTrainerEveryBars = 64;
    public const double MaxTrainerStepBpm = 20;

    public int TicksPerBeat => (int)Subdivision;

    public MetronomeSettings Normalized() => this with
    {
        Bpm = double.IsFinite(Bpm) ? Math.Clamp(Bpm, MinBpm, MaxBpm) : 120,
        BeatsPerBar = Math.Clamp(BeatsPerBar, 1, MaxBeatsPerBar),
        Subdivision = Enum.IsDefined(Subdivision) ? Subdivision : Subdivision.Quarters,
        Volume = float.IsFinite(Volume) ? Math.Clamp(Volume, 0f, MaxVolume) : 1f,
        GapPlayBars = Math.Clamp(GapPlayBars, 1, MaxGapBars),
        GapMuteBars = Math.Clamp(GapMuteBars, 1, MaxGapBars),
        TrainerStepBpm = double.IsFinite(TrainerStepBpm) ? Math.Clamp(TrainerStepBpm, 0.5, MaxTrainerStepBpm) : 2,
        TrainerEveryBars = Math.Clamp(TrainerEveryBars, 1, MaxTrainerEveryBars),
        TrainerTargetBpm = double.IsFinite(TrainerTargetBpm) ? Math.Clamp(TrainerTargetBpm, MinBpm, MaxBpm) : 160,
        Sound = Enum.IsDefined(Sound) ? Sound : MetronomeSound.Click,
        Drums = Drums?.Normalized(),
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
    private readonly float[] _kick;
    private readonly float[] _snare;
    private readonly float[] _hiHat;
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
    // Voices: a click lasts 35 ms (shorter than the fastest tick), but drum tails ring under the next hits.
    private const int MaxVoices = 8;
    private readonly float[]?[] _voice = new float[MaxVoices][];
    private readonly int[] _voicePos = new int[MaxVoices];
    private readonly float[] _voiceGain = new float[MaxVoices];
    private int _nextVoice;
    private float _softClipNorm;  // 1/tanh(gain) when gain > 1, else 0 (linear)

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
        _beatClick = ClickSynth.Click(sampleRate, 1000, 0.9f);
        _subdivisionClick = ClickSynth.Click(sampleRate, 800, 0.55f);
        _kick = DrumSynth.Kick(sampleRate);
        _snare = DrumSynth.Snare(sampleRate);
        _hiHat = DrumSynth.HiHat(sampleRate);
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
            Array.Clear(_voice);
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
            for (var v = 0; v < MaxVoices; v++)
            {
                if (_voice[v] is not { } sound) continue;
                value += sound[_voicePos[v]++] * _voiceGain[v];
                if (_voicePos[v] == sound.Length) _voice[v] = null;
            }
            if (_softClipNorm > 0) value = MathF.Tanh(value) * _softClipNorm; // peak stays ≤ 1, body gets louder
            output[i] = Math.Clamp(value, -1f, 1f);                           // overlapping drums can sum past full scale
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

        if (!_mutedBar)
        {
            if (s.Sound == MetronomeSound.Drums)
            {
                var hits = (s.Drums ?? DrumPattern.BasicRock).At(_beat, _subTick, ticksPerBeat);
                if ((hits & DrumHits.Kick) != 0) Trigger(_kick, s.Volume);
                if ((hits & DrumHits.Snare) != 0) Trigger(_snare, s.Volume);
                if ((hits & DrumHits.HiHat) != 0) Trigger(_hiHat, s.Volume);
            }
            else
            {
                Trigger(kind switch
                {
                    ClickKind.Accent => _accentClick,
                    ClickKind.Beat => _beatClick,
                    _ => _subdivisionClick,
                }, s.Volume);
            }
        }
        _softClipNorm = s.Volume > 1f ? 1f / MathF.Tanh(s.Volume) : 0f;

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

    /// <summary>Starts a sound in a free voice (all busy: replaces the oldest started).</summary>
    private void Trigger(float[] sound, float gain)
    {
        var slot = Array.IndexOf(_voice, null);
        if (slot < 0)
        {
            slot = _nextVoice;
            _nextVoice = (_nextVoice + 1) % MaxVoices;
        }
        _voice[slot] = sound;
        _voicePos[slot] = 0;
        _voiceGain[slot] = gain;
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

/// <summary>
/// Decaying-tone clicks with a little 2nd/3rd harmonic (brighter, cuts through a guitar), pre-rendered once so the audio
/// thread only copies samples. 35 ms with a 12 ms decay: +7.6 dB more energy than the original 15 ms / 4 ms pure-sine
/// click at default volume, +11.5 dB at 200 % (measured in tests; user: "the metronome should be louder"). 35 ms still
/// fits between 16ths at 400 BPM (37.5 ms).
/// </summary>
internal static class ClickSynth
{
    public static float[] Click(int sampleRate, double hz, float peak, double seconds = 0.035)
    {
        var n = (int)(sampleRate * seconds);
        var attack = Math.Max(1, (int)(0.0005 * sampleRate)); // 0.5 ms: crisp, but no DC step
        var release = Math.Max(1, (int)(0.002 * sampleRate)); // last 2 ms fade to exactly zero
        var data = new float[n];
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / sampleRate;
            var envelope = Math.Exp(-t / 0.012);
            if (i < attack) envelope *= (double)i / attack;
            if (i >= n - release) envelope *= (double)(n - 1 - i) / release;
            var phase = 2 * Math.PI * hz * t;
            data[i] = (float)(envelope * (Math.Sin(phase) + 0.35 * Math.Sin(2 * phase) + 0.2 * Math.Sin(3 * phase)));
        }

        // Normalise so the loudest sample is exactly `peak`.
        var max = 0f;
        foreach (var v in data) max = Math.Max(max, Math.Abs(v));
        if (max > 0)
            for (var i = 0; i < n; i++) data[i] = data[i] / max * peak;
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
