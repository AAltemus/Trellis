using GuitarPractice.Domain.Planning;

namespace GuitarPractice.Domain.Practice;

/// <summary>What happened during one <see cref="SessionTimer.Tick"/>.</summary>
[Flags]
public enum TimerTickResult
{
    None = 0,

    /// <summary>The whole-second elapsed value changed; refresh the display.</summary>
    ElapsedChanged = 1,

    /// <summary>The countdown reached zero on this tick: the item is now completed and the timer stopped. Ding.</summary>
    Finished = 2,

    /// <summary><see cref="SessionTimer.AutosaveInterval"/> of running time passed since the last save point.</summary>
    AutosaveDue = 4,
}

/// <summary>
/// Countdown for one practice item: Start / Pause / Stop (finish early) / Reset, plus a periodic <see cref="Tick"/>.
/// Time comes from <see cref="TimeProvider.GetTimestamp"/> (monotonic), never from counting ticks, so a late or
/// missed tick loses nothing. Fractions of a second are kept across pauses. UI-free: the caller owns persistence,
/// sound and selection, and reacts to the <see cref="TimerTickResult"/> flags.
/// </summary>
public sealed class SessionTimer
{
    public static readonly TimeSpan AutosaveInterval = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _clock;
    private TimeSpan _banked;          // practised time before the current run (from the log + earlier runs)
    private long _runStartTimestamp;   // when the current run started
    private TimeSpan _lastSaveRunTime; // running time of the current run at the last save point

    public SessionTimer(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    public bool IsLoaded { get; private set; }
    public bool IsRunning { get; private set; }
    public int PlannedMinutes { get; private set; }
    public int ElapsedSeconds { get; private set; }
    public bool Completed { get; private set; }

    public int RemainingSeconds => PracticeProgress.RemainingSeconds(PlannedMinutes, ElapsedSeconds, Completed);
    public bool IsDone => PracticeProgress.IsDone(PlannedMinutes, ElapsedSeconds, Completed);

    public bool CanStart => IsLoaded && !IsRunning && !IsDone;
    public bool CanPause => IsRunning;
    public bool CanStop => IsLoaded && !IsDone && (IsRunning || ElapsedSeconds > 0);
    public bool CanReset => IsLoaded && !IsRunning && (ElapsedSeconds > 0 || Completed);

    /// <summary>Selects an item with its saved progress. Not allowed while running (pause first).</summary>
    public void Load(int plannedMinutes, int elapsedSeconds, bool completed)
    {
        if (IsRunning) throw new InvalidOperationException("Pause the timer before loading another item.");
        IsLoaded = true;
        PlannedMinutes = Math.Max(0, plannedMinutes);
        _banked = TimeSpan.FromSeconds(Math.Max(0, elapsedSeconds));
        ElapsedSeconds = Math.Max(0, elapsedSeconds);
        Completed = completed;
    }

    /// <summary>Nothing selected. A running timer is paused first, so its time is kept in <see cref="ElapsedSeconds"/>.</summary>
    public void Unload()
    {
        Pause();
        IsLoaded = false;
    }

    /// <summary>
    /// The plan changed under the loaded item (regenerate). Keeps running; if the new plan is shorter than the time
    /// already practised, the next <see cref="Tick"/> finishes the item.
    /// </summary>
    public void UpdatePlannedMinutes(int plannedMinutes) => PlannedMinutes = Math.Max(0, plannedMinutes);

    public void Start()
    {
        if (!CanStart) throw new InvalidOperationException("Nothing to start: no item loaded, already running, or done.");
        _runStartTimestamp = _clock.GetTimestamp();
        _lastSaveRunTime = TimeSpan.Zero;
        IsRunning = true;
    }

    /// <summary>Holds the countdown. The caller should save afterwards (that is a save point for autosave).</summary>
    public void Pause()
    {
        if (!IsRunning) return;
        Sync();
        _banked = Clamp(_banked + RunTime());
        IsRunning = false;
    }

    /// <summary>Finishes the item early: keeps the time practised and marks it completed.</summary>
    public void Stop()
    {
        if (!IsLoaded) return;
        Pause();
        Completed = true;
    }

    /// <summary>Clears the item's progress. Not allowed while running.</summary>
    public void Reset()
    {
        if (IsRunning) throw new InvalidOperationException("Pause the timer before resetting.");
        _banked = TimeSpan.Zero;
        ElapsedSeconds = 0;
        Completed = false;
    }

    /// <summary>Call periodically (any rate) while running. Does nothing when not running.</summary>
    public TimerTickResult Tick()
    {
        if (!IsRunning) return TimerTickResult.None;

        var result = Sync() ? TimerTickResult.ElapsedChanged : TimerTickResult.None;

        if (RemainingSeconds == 0)
        {
            Pause();
            Completed = true;
            return result | TimerTickResult.Finished;
        }

        var runTime = RunTime();
        if (runTime - _lastSaveRunTime >= AutosaveInterval)
        {
            _lastSaveRunTime = runTime;
            result |= TimerTickResult.AutosaveDue;
        }
        return result;
    }

    private TimeSpan RunTime() => _clock.GetElapsedTime(_runStartTimestamp);

    private TimeSpan Clamp(TimeSpan practised)
    {
        var planned = TimeSpan.FromMinutes(PlannedMinutes);
        return practised > planned ? planned : practised;
    }

    /// <returns>True when the whole-second elapsed value changed.</returns>
    private bool Sync()
    {
        var elapsed = (int)Clamp(_banked + RunTime()).TotalSeconds;
        if (elapsed == ElapsedSeconds) return false;
        ElapsedSeconds = elapsed;
        return true;
    }
}

/// <summary>Which item to line up after one finishes.</summary>
public static class SessionOrder
{
    /// <summary>
    /// The first unfinished item after <paramref name="currentIndex"/>, else the first unfinished one before it;
    /// -1 when everything is done.
    /// </summary>
    public static int NextUnfinished<T>(IReadOnlyList<T> items, int currentIndex, Func<T, bool> isDone)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(isDone);
        for (var i = currentIndex + 1; i < items.Count; i++)
            if (!isDone(items[i])) return i;
        for (var i = 0; i <= currentIndex && i < items.Count; i++)
            if (!isDone(items[i])) return i;
        return -1;
    }
}
