namespace GuitarPractice.Audio;

public enum BendPhase
{
    /// <summary>Nothing playing.</summary>
    Idle,

    /// <summary>Fretted note sounding, not bent yet (under a quarter of the way).</summary>
    Unbent,

    /// <summary>On the way up, more than 50 ¢ short of the target.</summary>
    Bending,

    /// <summary>Within ±50 ¢ of the target: the hold is being scored.</summary>
    Holding,

    /// <summary>More than 50 ¢ past the target.</summary>
    Overbent,
}

/// <summary>A finished bend: time-weighted average offset from the target while it was held.</summary>
public sealed record BendAttempt(int ReferenceMidi, int TargetMidi, double AverageCents, TimeSpan Held);

public sealed record BendState(
    BendPhase Phase,
    int? ReferenceMidi,
    int? TargetMidi,
    double CentsFromTarget,
    double Progress,
    BendAttempt? Completed);

/// <summary>
/// Bend-accuracy trainer. The first pitch after silence becomes the reference (snapped to the nearest semitone); the
/// target is reference + <see cref="IntervalSemitones"/>. While the pitch is within ±50 ¢ of the target the offset is
/// recorded; the score is its time-weighted average with the first and last <see cref="EdgeTrim"/> dropped (that's the
/// glide into and out of the window, not where the bend settled; vibrato in between counts). When the bend leaves that window (release, overbend) or the note stops, a hold of at least
/// <see cref="MinimumHold"/> is reported once as <see cref="BendState.Completed"/>. Dropping more than half a semitone
/// below the reference counts as a new fretted note (re-reference). Short dropouts (under <see cref="GapTolerance"/>)
/// keep the reference and pause the hold instead of ending it, since a real string's pitch track blinks out for a frame or
/// two during a fast bend or as it decays. Pure: the caller supplies time.
/// </summary>
public sealed class BendTracker
{
    public static readonly TimeSpan MinimumHold = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan EdgeTrim = TimeSpan.FromMilliseconds(80);
    public static readonly TimeSpan GapTolerance = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan MaxFrameGap = TimeSpan.FromMilliseconds(100); // longer gaps add no hold time
    private const double HoldWindowCents = 50;

    private int? _reference;
    private TimeSpan? _lastVoiced;
    private BendState? _lastState;
    private readonly List<(double Cents, double Seconds)> _hold = [];
    private TimeSpan _held;
    private int _interval = 2;

    /// <summary>Bend size in semitones (1 = half step, 2 = whole step, 3 = 1½ steps, 4 = 2 steps).</summary>
    public int IntervalSemitones
    {
        get => _interval;
        set
        {
            _interval = Math.Clamp(value, 1, 4);
            ResetHold();
        }
    }

    public double A4 { get; set; } = 440;

    public void Reset()
    {
        _reference = null;
        _lastVoiced = null;
        _lastState = null;
        ResetHold();
    }

    /// <param name="hz">Current (smoothed) pitch, or null when nothing is sounding.</param>
    /// <param name="now">Monotonic time of this reading.</param>
    public BendState Update(double? hz, TimeSpan now)
    {
        if (hz is not > 0)
        {
            // A brief dropout: keep showing the last state, keep the reference, pause the hold.
            if (_reference is not null && _lastVoiced is { } voiced && now - voiced < GapTolerance && _lastState is { } shown)
                return shown with { Completed = null };

            var finished = FinishHold();
            _reference = null;
            _lastVoiced = null;
            _lastState = null;
            return new BendState(BendPhase.Idle, null, null, 0, 0, finished);
        }

        var dt = _lastVoiced is { } last && now > last ? now - last : TimeSpan.Zero;
        if (dt > MaxFrameGap) dt = TimeSpan.Zero;
        _lastVoiced = now;

        var midi = Pitch.MidiFromFrequency(hz.Value, A4);
        BendAttempt? completed = null;
        if (_reference is null || midi < _reference.Value - 0.5)
        {
            completed = FinishHold(); // a new fretted note ends any bend in progress
            _reference = (int)Math.Round(midi);
        }

        var reference = _reference.Value;
        var target = reference + IntervalSemitones;
        var centsFromTarget = (midi - target) * 100;
        var progress = (midi - reference) / IntervalSemitones;

        BendPhase phase;
        if (Math.Abs(centsFromTarget) <= HoldWindowCents)
        {
            phase = BendPhase.Holding;
            _hold.Add((centsFromTarget, dt.TotalSeconds));
            _held += dt;
        }
        else
        {
            completed ??= FinishHold();
            phase = centsFromTarget > HoldWindowCents ? BendPhase.Overbent
                : progress < 0.25 ? BendPhase.Unbent
                : BendPhase.Bending;
        }

        var state = new BendState(phase, reference, target, centsFromTarget, progress, completed);
        _lastState = state;
        return state;
    }

    private BendAttempt? FinishHold()
    {
        BendAttempt? attempt = null;
        if (_reference is int reference && _held >= MinimumHold)
            attempt = new BendAttempt(reference, reference + IntervalSemitones, ScoreHold(), _held);
        ResetHold();
        return attempt;
    }

    /// <summary>Time-weighted mean cents, trimming <see cref="EdgeTrim"/> off both ends when the hold is long enough.</summary>
    private double ScoreHold()
    {
        var trim = _held >= MinimumHold + 2 * EdgeTrim ? EdgeTrim.TotalSeconds : 0;
        var end = _held.TotalSeconds - trim;
        double t = 0, weighted = 0, weight = 0;
        foreach (var (cents, seconds) in _hold)
        {
            // Overlap of [t, t + seconds] with [trim, end].
            var overlap = Math.Min(t + seconds, end) - Math.Max(t, trim);
            if (overlap > 0)
            {
                weighted += cents * overlap;
                weight += overlap;
            }
            t += seconds;
        }
        return weight > 0 ? weighted / weight : _hold.Count > 0 ? _hold.Average(h => h.Cents) : 0;
    }

    private void ResetHold()
    {
        _hold.Clear();
        _held = TimeSpan.Zero;
    }
}
