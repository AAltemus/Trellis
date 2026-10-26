namespace GuitarPractice.Audio;

/// <summary>
/// One take of the rhythm trainer, in ticks: a bar of <see cref="BarTicks"/> with clicks on <see cref="BeatTicks"/>
/// (the first accented) and the rhythm's notes at <see cref="NoteOnsets"/> lasting <see cref="NoteLengths"/>.
/// <see cref="SecondsPerTick"/> sets the tempo. The take is a count-in bar, then (with <see cref="ListenFirst"/>) one
/// bar where the rhythm is played for you, then <see cref="PlayBars"/> bars for you to play (recorded).
/// </summary>
public sealed record RhythmTrainerPlan(
    double SecondsPerTick,
    int BarTicks,
    IReadOnlyList<int> BeatTicks,
    IReadOnlyList<int> NoteOnsets,
    IReadOnlyList<int> NoteLengths,
    bool ListenFirst = true,
    int PlayBars = 2,
    int HatTicks = 240);

/// <summary>Where a take is.</summary>
public enum RhythmTrainerState
{
    Stopped,
    CountIn,
    Listen,
    Play,

    /// <summary>The last bar's tail has been recorded: <see cref="RhythmTrainerEngine.Recording"/> is complete.</summary>
    Done,
}

/// <summary>
/// Plays a rhythm-trainer take on a full-duplex stream (click, drums and, in the listen bar, the rhythm as a muted power
/// chord) and records the input during your bars. Like the <see cref="Looper"/>, what you play reaches the input
/// <see cref="LatencySamples"/> after you heard the click, so the input is filed at the moment that was being heard: a
/// note played on the click lands on the beat. <see cref="Recording"/> starts <see cref="PreRollSeconds"/> before your
/// first bar, so a first note played a little early is still caught.
/// Every sound is placed at its exact sample. <see cref="Process"/> doesn't allocate, lock or block.
/// </summary>
public sealed class RhythmTrainerEngine
{
    public const int CountInBars = 1;
    private const int MaxVoices = 16;
    private const double TailSeconds = 0.3;

    /// <summary>How much of the recording comes before your first downbeat (the scoring window's width).</summary>
    public const double PreRollSeconds = 0.1;

    private readonly float[][] _sounds;     // by SoundId
    private readonly long[] _eventTimes;    // sorted
    private readonly byte[] _eventSounds;
    private readonly float[] _recording;
    private readonly float[]?[] _voice = new float[MaxVoices][];
    private readonly int[] _voicePos = new int[MaxVoices];
    private readonly byte[] _voiceSound = new byte[MaxVoices];

    private int _startRequest;
    private int _stopRequest;
    private bool _running;
    private long _sample;
    private int _nextEvent;
    private int _nextVoice;

    private long _publishedSample;
    private int _publishedRunning;

    private enum SoundId : byte { Accent, Click, Kick, Snare, HiHat, Rhythm }

    public RhythmTrainerEngine(int sampleRate, RhythmTrainerPlan plan)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfLessThan(plan.BarTicks, 1);
        SampleRate = sampleRate;
        Plan = plan;
        ListenBars = plan.ListenFirst ? 1 : 0;
        PlayBars = Math.Clamp(plan.PlayBars, 1, 16);

        _sounds = new float[6][];
        _sounds[(int)SoundId.Accent] = ClickSynth.Click(sampleRate, 1500, 1.0f);
        _sounds[(int)SoundId.Click] = ClickSynth.Click(sampleRate, 1000, 0.85f);
        _sounds[(int)SoundId.Kick] = DrumSynth.Kick(sampleRate);
        _sounds[(int)SoundId.Snare] = DrumSynth.Snare(sampleRate);
        _sounds[(int)SoundId.HiHat] = DrumSynth.HiHat(sampleRate);
        _sounds[(int)SoundId.Rhythm] = RenderRhythm(sampleRate, plan);

        var events = new List<(long Time, SoundId Sound)>();
        var bars = CountInBars + ListenBars + PlayBars;
        for (var bar = 0; bar < bars; bar++)
        {
            for (var b = 0; b < plan.BeatTicks.Count; b++)
                events.Add((At(bar, plan.BeatTicks[b]), b == 0 ? SoundId.Accent : SoundId.Click));
            if (bar < CountInBars) continue;
            foreach (var (tick, hits) in Groove(plan))
            {
                if ((hits & DrumHits.Kick) != 0) events.Add((At(bar, tick), SoundId.Kick));
                if ((hits & DrumHits.Snare) != 0) events.Add((At(bar, tick), SoundId.Snare));
                if ((hits & DrumHits.HiHat) != 0) events.Add((At(bar, tick), SoundId.HiHat));
            }
            if (bar < CountInBars + ListenBars) events.Add((At(bar, 0), SoundId.Rhythm));
        }
        events.Sort((a, b) => a.Time.CompareTo(b.Time));
        _eventTimes = [.. events.Select(e => e.Time)];
        _eventSounds = [.. events.Select(e => (byte)e.Sound)];

        PlayStart = At(CountInBars + ListenBars, 0);
        PlayEnd = At(bars, 0);
        PreRollSamples = (int)Math.Min(PreRollSeconds * sampleRate, PlayStart);
        _recording = new float[PreRollSamples + PlayEnd - PlayStart + (int)(TailSeconds * sampleRate)];
    }

    public int SampleRate { get; }
    public RhythmTrainerPlan Plan { get; }
    public int ListenBars { get; }
    public int PlayBars { get; }

    /// <summary>Stream sample of your first bar's downbeat (the recording's sample 0).</summary>
    public long PlayStart { get; }

    /// <summary>Samples of <see cref="Recording"/> before <see cref="PlayStart"/>.</summary>
    public int PreRollSamples { get; }

    /// <summary>Stream sample where your last bar ends.</summary>
    public long PlayEnd { get; }

    /// <summary>Round-trip latency to compensate (samples); set before or during the take.</summary>
    public int LatencySamples { get; set; }

    public bool ClickEnabled { get; set; } = true;
    public bool DrumsEnabled { get; set; } = true;
    public float ClickVolume { get; set; } = 0.8f;
    public float DrumVolume { get; set; } = 0.8f;
    public float RhythmVolume { get; set; } = 0.9f;

    /// <summary>Your bars as heard (latency removed), from <see cref="PreRollSamples"/> before the first downbeat to a short
    /// tail after the last bar. Complete once <see cref="State"/> is Done.</summary>
    public float[] Recording => _recording;

    /// <summary>Stream sample of tick <paramref name="tick"/> of bar <paramref name="bar"/> (bar 0 = the count-in).</summary>
    public long At(int bar, int tick) => (long)Math.Round(((double)bar * Plan.BarTicks + tick) * Plan.SecondsPerTick * SampleRate);

    public RhythmTrainerState State
    {
        get
        {
            if (Volatile.Read(ref _publishedRunning) == 0) return RhythmTrainerState.Stopped;
            var s = Volatile.Read(ref _publishedSample);
            if (s - Math.Max(0, LatencySamples) - (PlayStart - PreRollSamples) >= _recording.Length) return RhythmTrainerState.Done; // all of it recorded
            if (s < At(CountInBars, 0)) return RhythmTrainerState.CountIn;
            return s < PlayStart ? RhythmTrainerState.Listen : RhythmTrainerState.Play;
        }
    }

    /// <summary>The bar being heard (0 = count-in) and the tick within it.</summary>
    public (int Bar, double Tick) Position
    {
        get
        {
            var ticks = Volatile.Read(ref _publishedSample) / (Plan.SecondsPerTick * SampleRate);
            var bar = (int)(ticks / Plan.BarTicks);
            return (bar, ticks - (double)bar * Plan.BarTicks);
        }
    }

    public void Start() => Volatile.Write(ref _startRequest, 1);

    public void Stop() => Volatile.Write(ref _stopRequest, 1);

    /// <summary>The duplex callback: <paramref name="input"/> and <paramref name="output"/> are the same instant.</summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (Interlocked.Exchange(ref _stopRequest, 0) == 1) _running = false;
        if (Interlocked.Exchange(ref _startRequest, 0) == 1)
        {
            _running = true;
            _sample = 0;
            _nextEvent = 0;
            Array.Clear(_voice);
            Array.Clear(_recording);
        }
        if (!_running)
        {
            output.Clear();
            Publish();
            return;
        }

        var latency = Math.Max(0, LatencySamples);
        var countInEnd = At(CountInBars, 0);
        for (var i = 0; i < output.Length; i++)
        {
            var s = _sample + i;
            while (_nextEvent < _eventTimes.Length && _eventTimes[_nextEvent] <= s)
            {
                var sound = _eventSounds[_nextEvent++];
                _voice[_nextVoice] = _sounds[sound];
                _voicePos[_nextVoice] = 0;
                _voiceSound[_nextVoice] = sound;
                _nextVoice = (_nextVoice + 1) % MaxVoices;
            }

            var value = 0f;
            for (var v = 0; v < MaxVoices; v++)
            {
                if (_voice[v] is not { } data) continue;
                value += data[_voicePos[v]] * Gain((SoundId)_voiceSound[v], s < countInEnd);
                if (++_voicePos[v] >= data.Length) _voice[v] = null;
            }
            output[i] = SoftLimit(value);

            var heard = s - latency - (PlayStart - PreRollSamples);
            if (heard >= 0 && heard < _recording.Length && i < input.Length) _recording[heard] = input[i];
        }
        _sample += output.Length;
        Publish();
    }

    private float Gain(SoundId sound, bool countIn) => sound switch
    {
        SoundId.Accent or SoundId.Click => countIn ? Math.Max(ClickVolume, 0.6f) : ClickEnabled ? ClickVolume : 0f,
        SoundId.Rhythm => RhythmVolume,
        _ => DrumsEnabled ? DrumVolume : 0f,
    };

    private void Publish()
    {
        Volatile.Write(ref _publishedSample, _sample);
        Volatile.Write(ref _publishedRunning, _running ? 1 : 0);
    }

    /// <summary>
    /// The drum part for one bar: closed hi-hat every <see cref="RhythmTrainerPlan.HatTicks"/> (8ths), kick on the
    /// first beat, then snare and kick alternating on the other beats (4/4: K S K S; 6/8: K S; 7/8 2+2+3: K S K).
    /// </summary>
    public static IReadOnlyList<(int Tick, DrumHits Hits)> Groove(RhythmTrainerPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var hits = new SortedDictionary<int, DrumHits>();
        void Add(int tick, DrumHits h) => hits[tick] = hits.GetValueOrDefault(tick) | h;
        for (var t = 0; t < plan.BarTicks; t += Math.Max(1, plan.HatTicks)) Add(t, DrumHits.HiHat);
        for (var b = 0; b < plan.BeatTicks.Count; b++) Add(plan.BeatTicks[b], b % 2 == 0 ? DrumHits.Kick : DrumHits.Snare);
        return [.. hits.Select(kv => (kv.Key, kv.Value))];
    }

    /// <summary>The rhythm as a muted E5 power chord (E2 B2 E3), each note held for its written length (up to 1.2 s).</summary>
    private static float[] RenderRhythm(int sampleRate, RhythmTrainerPlan plan)
    {
        var notes = new List<SynthNote>();
        for (var i = 0; i < plan.NoteOnsets.Count; i++)
        {
            var start = plan.NoteOnsets[i] * plan.SecondsPerTick;
            var length = Math.Clamp((i < plan.NoteLengths.Count ? plan.NoteLengths[i] : 0) * plan.SecondsPerTick * 0.9, 0.06, 1.2);
            foreach (var midi in new[] { 40, 47, 52 }) notes.Add(new SynthNote(start, midi, length));
        }
        return notes.Count == 0 ? [] : PluckSynth.Render(sampleRate, notes);
    }

    private static float SoftLimit(float x)
    {
        const float Knee = 0.8f;
        var m = MathF.Abs(x);
        if (m <= Knee) return x;
        var limited = Knee + (1 - Knee) * MathF.Tanh((m - Knee) / (1 - Knee));
        return x < 0 ? -limited : limited;
    }
}

/// <summary>
/// Finds note attacks in a recording (offline), on 1 ms frames, from two cues; whichever fires first marks the onset,
/// with at least <c>minGapSeconds</c> before the next one:
/// <list type="bullet">
/// <item><b>Level rise</b>: the signal's loudness (peak over <see cref="LevelHoldSeconds"/>, which smooths out each
/// string's own cycle; |x| repeats every half period, so this holds even for a 7-string's B1) is at least
/// <see cref="LevelRiseDb"/> above what it was <see cref="LevelLagSeconds"/> earlier. Catches notes after a mute or a
/// decay: the usual case for rhythm playing (user's take 2026-10-26: muted between 8ths, attacks +13 dB, but the pick
/// click only +5 dB over the ring).</item>
/// <item><b>Attack click</b>: the first difference (the pick's broadband click) jumps at least <see cref="RiseDb"/>
/// above its loudest frame in the <see cref="HoldSeconds"/> ending 4 ms before. Catches re-strikes over a string still
/// ringing at full level.</item>
/// </list>
/// Both must clear a gate 20 dB below the loudest moment. The onset is placed where the attack starts: for a level rise,
/// the first sample reaching half the new peak; for a click, the first reaching 30 % of the click's peak.
/// </summary>
public static class OnsetDetector
{
    public const double RiseDb = 9;
    public const double LevelRiseDb = 6;
    public const double LevelHoldSeconds = 0.015;
    public const double LevelLagSeconds = 0.02;

    /// <summary>Longer than one period of the lowest guitar string (B1 on a 7-string: 31 Hz, 32 ms).</summary>
    public const double HoldSeconds = 0.035;

    private const double FrameSeconds = 0.001;
    private const int SkipFrames = 4; // 4 ms: a note's own rising click doesn't hide it

    /// <summary>
    /// Thresholds of the faint pass in <see cref="DetectNear"/>, which also needs both cues together (louder AND brighter):
    /// on the user's gallop take this found 46/48 notes (vs 31 strict), with no false credit on a ringing, beating
    /// chord; either cue alone at these levels credited 5–6 notes that were never played (sweep, 2026-10-26).
    /// </summary>
    public const double SensitiveLevelRiseDb = 2;
    public const double SensitiveClickRiseDb = 4;

    /// <summary>
    /// Onsets for scoring against known note times: every clear onset (<see cref="Detect"/>), plus, for each expected
    /// note with no clear onset within <paramref name="windowSeconds"/>, the nearest faint one in that window (a
    /// <see cref="SensitiveLevelRiseDb"/> + <see cref="SensitiveClickRiseDb"/> pass, both cues required) that isn't part of a clear one.
    /// A 16th played right after another on a ringing low string adds only 2–3 dB (user's gallop take, 2026-10-26):
    /// too little to find anywhere, but plain where a note is due. Times are in the signal's seconds, ascending.
    /// </summary>
    public static IReadOnlyList<double> DetectNear(ReadOnlySpan<float> signal, int sampleRate, IReadOnlyList<double> expectedSeconds,
        double windowSeconds, double minGapSeconds = 0.05)
    {
        ArgumentNullException.ThrowIfNull(expectedSeconds);
        var clear = Detect(signal, sampleRate, minGapSeconds);
        var faint = Detect(signal, sampleRate, minGapSeconds, SensitiveLevelRiseDb, SensitiveClickRiseDb, requireBoth: true);
        var result = new List<double>(clear);
        foreach (var e in expectedSeconds)
        {
            if (clear.Any(c => Math.Abs(c - e) <= windowSeconds)) continue;
            var best = double.NaN;
            foreach (var f in faint)
            {
                if (Math.Abs(f - e) > windowSeconds || result.Any(r => Math.Abs(r - f) < 0.03)) continue;
                if (double.IsNaN(best) || Math.Abs(f - e) < Math.Abs(best - e)) best = f;
            }
            if (double.IsNaN(best)) best = ShapeChange(signal, sampleRate, e, windowSeconds);
            if (!double.IsNaN(best) && !result.Any(r => Math.Abs(r - best) < 0.03)) result.Add(best);
        }
        result.Sort();
        return result;
    }

    /// <summary>Below this, one cycle no longer matches the previous one: the string was picked again.</summary>
    public const double ShapeBreak = 0.6;

    /// <summary>The string must have been this steady (cycle to cycle) before the window for <see cref="ShapeChange"/>.</summary>
    public const double ShapeSteady = 0.8;

    /// <summary>
    /// A re-pick of a string that's already ringing, found by its shape: picking a vibrating string can add almost no
    /// energy (no louder, no brighter: user's 16ths at 140, 2026-10-26), but it always restarts the vibration, so one
    /// period stops matching the next. Learns the period (2–35 ms, the best match) just before the window, needs the
    /// string to have been steady there (≥ <see cref="ShapeSteady"/>), then looks for the first moment in the window
    /// where the coming period doesn't match the last one (&lt; <see cref="ShapeBreak"/>), about a third of a period
    /// into the new vibration, so the pick is placed two thirds of a period after it. NaN when there's no such moment.
    /// </summary>
    public static double ShapeChange(ReadOnlySpan<float> signal, int sampleRate, double dueSeconds, double windowSeconds)
    {
        var due = (int)(dueSeconds * sampleRate);
        var window = (int)(windowSeconds * sampleRate);
        var minLag = (int)(0.002 * sampleRate);
        var maxLag = (int)(0.035 * sampleRate);
        var reference = (int)(0.02 * sampleRate);

        // The period, from the 20 ms just before the window (and the steadiness there).
        var refEnd = due - window - (int)(0.005 * sampleRate);
        if (refEnd - reference - maxLag < 0 || due + window + maxLag >= signal.Length) return double.NaN;
        var lag = 0;
        var steadiness = 0.0;
        for (var l = minLag; l <= maxLag; l++)
        {
            var c = Correlation(signal, refEnd - reference, refEnd - reference - l, reference);
            if (c > steadiness)
            {
                steadiness = c;
                lag = l;
            }
        }
        if (steadiness < ShapeSteady || lag == 0) return double.NaN;

        // The match falls below the threshold once about a third of the coming period is the new vibration, so the
        // pick is two thirds of a period after that moment (the placement that puts a synthetic re-pick on time).
        var step = Math.Max(1, sampleRate / 1000);
        for (var t = due - window - lag; t <= due + window - lag; t += step)
            if (Correlation(signal, t, t - lag, lag) < ShapeBreak)
                return (t + 2 * lag / 3) / (double)sampleRate;
        return double.NaN;
    }

    /// <summary>Normalised correlation of signal[a, a+n) with signal[b, b+n).</summary>
    private static double Correlation(ReadOnlySpan<float> signal, int a, int b, int n)
    {
        double ab = 0, aa = 0, bb = 0;
        for (var i = 0; i < n; i++)
        {
            double x = signal[a + i], y = signal[b + i];
            ab += x * y;
            aa += x * x;
            bb += y * y;
        }
        return aa < 1e-12 || bb < 1e-12 ? 0 : ab / Math.Sqrt(aa * bb);
    }

    /// <param name="levelRiseDb">Level-rise cue threshold (<see cref="LevelRiseDb"/> by default).</param>
    /// <param name="clickRiseDb">Attack-click cue threshold (<see cref="RiseDb"/> by default).</param>
    /// <param name="requireBoth">Only level rises that also brighten (the click cue fires within them).</param>
    public static IReadOnlyList<double> Detect(ReadOnlySpan<float> signal, int sampleRate, double minGapSeconds = 0.05,
        double levelRiseDb = LevelRiseDb, double clickRiseDb = RiseDb, bool requireBoth = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        var hop = Math.Max(1, (int)(FrameSeconds * sampleRate));
        var frames = signal.Length / hop;
        if (frames < 2) return [];

        var peak = new double[frames];   // |x|
        var click = new double[frames];  // |first difference|
        var previous = 0f;
        for (var f = 0; f < frames; f++)
        {
            double p = 0, d = 0;
            for (var i = f * hop; i < (f + 1) * hop; i++)
            {
                p = Math.Max(p, Math.Abs(signal[i]));
                d = Math.Max(d, Math.Abs(signal[i] - previous));
                previous = signal[i];
            }
            peak[f] = p;
            click[f] = d;
        }

        var levelHold = Math.Max(1, (int)Math.Round(LevelHoldSeconds / FrameSeconds));
        var level = new double[frames]; // peak over the last LevelHoldSeconds
        for (var f = 0; f < frames; f++)
        {
            var m = 0.0;
            for (var k = Math.Max(0, f - levelHold + 1); k <= f; k++) m = Math.Max(m, peak[k]);
            level[f] = m;
        }
        var loudest = level.Max();
        if (loudest < 1e-4) return [];
        var gate = Math.Max(0.001, loudest * 0.1); // −20 dB
        var loudestClick = click.Max();
        var clickGate = Math.Max(0.0005, loudestClick * 0.1);

        var lag = Math.Max(1, (int)Math.Round(LevelLagSeconds / FrameSeconds));
        var hold = Math.Max(2, (int)Math.Round(HoldSeconds / FrameSeconds));
        var gapFrames = Math.Max(1, (int)(minGapSeconds / FrameSeconds));
        var levelRatio = Math.Pow(10, levelRiseDb / 20);
        var clickRatio = Math.Pow(10, clickRiseDb / 20);
        bool ClickRises(int k)
        {
            if (click[k] < clickGate || peak[k] < gate * 0.5) return false;
            var before = 0.0;
            for (var j = Math.Max(0, k - SkipFrames - hold); j < k - SkipFrames; j++) before = Math.Max(before, click[j]);
            return click[k] >= clickRatio * Math.Max(before, 1e-6);
        }

        var onsets = new List<double>();
        var last = -gapFrames;
        for (var f = 1; f < frames; f++)
        {
            if (f - last < gapFrames) continue;

            var byLevel = f >= lag && level[f] >= gate && level[f] >= levelRatio * Math.Max(level[f - lag], 1e-6);
            var brightAt = -1;
            if (requireBoth)
            {
                // Louder and brighter together: the click must also rise within the level rise's span.
                if (!byLevel) continue;
                for (var k = Math.Max(1, f - levelHold); k <= Math.Min(frames - 1, f + 5) && brightAt < 0; k++) // a louder cycle can come a period after the pick
                    if (ClickRises(k)) brightAt = k;
                if (brightAt < 0) continue;
            }
            var byClick = !byLevel && ClickRises(f);
            if (!byLevel && !byClick) continue;

            int at;
            if (brightAt >= 0)
                at = ClickOnset(signal, hop, brightAt); // a faint rise: the pick's click is where it starts
            else if (byClick)
                at = ClickOnset(signal, hop, f);
            else
            {
                // The rise happened within the hold: from there, the first sample that clearly leaves what was ringing
                // before (1.5× its peak) and reaches a quarter of the new peak. (Half the peak waited for the first big
                // swing, up to most of a low E's 12 ms cycle after the note started.)
                var from = Math.Max(0, (f - levelHold - 1) * hop);
                var to = Math.Min(signal.Length, (f + levelHold) * hop);
                var top = 0f;
                for (var i = f * hop; i < to; i++) top = Math.Max(top, Math.Abs(signal[i]));
                var threshold = Math.Max(top * 0.25f, 1.5f * (float)level[f - lag]);
                at = from;
                while (at < to && Math.Abs(signal[at]) < threshold) at++;
            }
            onsets.Add((double)at / sampleRate);
            last = Math.Max(f, at / hop); // the refractory counts from the attack itself
        }
        return onsets;
    }

    /// <summary>A click's peak within 10 ms of frame <paramref name="k"/>, then the first sample from just before it reaching 30 % of that peak.</summary>
    private static int ClickOnset(ReadOnlySpan<float> signal, int hop, int k)
    {
        var from = Math.Max(1, (k - 1) * hop);
        var to = Math.Min(signal.Length, (k + 10) * hop);
        var top = 0f;
        for (var i = from; i < to; i++) top = Math.Max(top, Math.Abs(signal[i] - signal[i - 1]));
        var at = from;
        while (at < to && Math.Abs(signal[at] - signal[at - 1]) < top * 0.3f) at++;
        return at;
    }
}
