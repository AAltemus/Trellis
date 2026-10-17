using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GuitarPractice.Domain.Planning;
using GuitarPractice.Domain.Practice;
using GuitarPractice.Infrastructure;

namespace GuitarPractice.App.ViewModels;

/// <summary>
/// Practice session runner: select a day/item, Start / Pause / Stop / Reset a countdown.
/// The countdown itself is <see cref="SessionTimer"/> (pure, tested); this partial only mirrors its state onto the
/// selected <see cref="EntryViewModel"/>, saves to PracticeLogs, plays the ding and lines up the next item.
/// The DispatcherTimer just drives <see cref="SessionTimer.Tick"/>, so a late tick never loses time.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private SessionTimer _timer = null!;
    private DispatcherTimer _ticker = null!;
    private PracticeKey? _selectedKey;

    [ObservableProperty]
    private EntryViewModel? _current;

    [ObservableProperty]
    private bool _isRunning;

    public bool HasSelection => Current is not null;

    public string TimerTitle => Current?.Name ?? "Pick something to practise";

    public string TimerSubtitle => Current is null
        ? "Click an item in the week, or a day's name to start with its first unfinished item."
        : $"{Current.Category} · {Current.Day.Header} · {Current.PlannedMinutes} min planned";

    public string TimerRemaining => Current is null ? "--:--" : PracticeProgress.Format(Current.RemainingSeconds);

    public double TimerProgress => Current?.Fraction ?? 0;

    public string StartLabel => Current is { ElapsedSeconds: > 0, IsDone: false } ? "Resume" : "Start";

    public string DayRemaining => Current is null ? "" : $"{Current.Day.Header}: {Current.Day.RemainingLabel}";

    private void InitializeSession()
    {
        _timer = new SessionTimer(_clock);
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _ticker.Tick += (_, _) => Tick();
    }

    // ---- Selection ----

    internal void Select(EntryViewModel entry)
    {
        if (ReferenceEquals(entry, Current)) return;
        if (IsRunning && Current is not null) PauseAndSave(Current); // switching items pauses and saves the running one
        Current = entry;
    }

    internal void SelectDay(DayViewModel day)
    {
        var first = day.Entries.FirstOrDefault(e => !e.IsDone) ?? day.Entries.FirstOrDefault();
        if (first is not null) Select(first);
    }

    partial void OnCurrentChanged(EntryViewModel? oldValue, EntryViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
            oldValue.IsRunning = false;
            oldValue.Day.IsSelected = false;
        }
        if (newValue is not null)
        {
            newValue.IsSelected = true;
            newValue.IsRunning = IsRunning;
            newValue.Day.IsSelected = true;
        }

        // Only RelinkSession swaps the entry while running (same item, rebuilt view model): keep the run going.
        if (newValue is null) _timer.Unload();
        else if (_timer.IsRunning) _timer.UpdatePlannedMinutes(newValue.PlannedMinutes);
        else _timer.Load(newValue.PlannedMinutes, newValue.ElapsedSeconds, newValue.Completed);

        _selectedKey = newValue?.Key;
        RefreshTimer();
        LoadProgressLog();
    }

    /// <summary>After the week regenerates, point the session at the new view model for the same (date, item).</summary>
    private void RelinkSession()
    {
        if (_selectedKey is not PracticeKey key)
        {
            RefreshTimer();
            return;
        }

        var match = Days.SelectMany(d => d.Entries).FirstOrDefault(e => e.Key == key);
        if (match is null && IsRunning && Current is not null) PauseAndSave(Current);
        Current = match;
    }

    // ---- Commands ----

    private bool CanStart() => Current is not null && _timer.CanStart;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        _timer.Start();
        _ticker.Start();
        IsRunning = true;
        Current!.IsRunning = true;
        RefreshTimer();
    }

    private bool CanPause() => _timer.CanPause;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        if (Current is not null) PauseAndSave(Current);
        RefreshTimer();
    }

    private bool CanStop() => Current is not null && _timer.CanStop;

    /// <summary>Finish the item early: keeps the time practised, marks it done, takes it off the day's remaining time.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (Current is not { } entry) return;
        _timer.Stop();
        Finish(entry, ding: false);
    }

    private bool CanReset() => Current is not null && _timer.CanReset;

    [RelayCommand(CanExecute = nameof(CanReset))]
    private void Reset()
    {
        var entry = Current!;
        _timer.Reset();
        ApplyTimer(entry);
        _progress.Remove(entry.Key);
        _logStore.Reset(entry.Key);
        entry.Day.RefreshRemaining();
        RefreshTimer();
    }

    [RelayCommand]
    private void TestDing() => _sound.PlayDing();

    /// <summary>Called when the window closes so a running timer doesn't lose its last seconds.</summary>
    public void SaveOnExit()
    {
        if (IsRunning && Current is not null) PauseAndSave(Current);
    }

    // ---- Timer plumbing ----

    private void Tick()
    {
        var result = _timer.Tick();
        if (Current is not { } entry)
        {
            StopTicking();
            RefreshTimer();
            return;
        }

        if (result.HasFlag(TimerTickResult.Finished))
        {
            Finish(entry, ding: true);
            return;
        }

        if (result.HasFlag(TimerTickResult.ElapsedChanged))
        {
            ApplyTimer(entry);
            entry.Day.RefreshRemaining();
            RefreshTimer();
        }

        if (result.HasFlag(TimerTickResult.AutosaveDue)) Persist(entry);
    }

    /// <summary>The timer has already completed (zero reached or Stop); save, ding, line up the next item.</summary>
    private void Finish(EntryViewModel entry, bool ding)
    {
        StopTicking();
        ApplyTimer(entry);
        Persist(entry);
        entry.Day.RefreshRemaining();
        if (ding) _sound.PlayDing();

        // Line up the next unfinished item on the same day (not started automatically).
        var entries = entry.Day.Entries;
        var next = SessionOrder.NextUnfinished(entries, entries.IndexOf(entry), e => e.IsDone);
        if (next >= 0) Current = entries[next];
        RefreshTimer();
    }

    private void PauseAndSave(EntryViewModel entry)
    {
        _timer.Pause();
        StopTicking();
        ApplyTimer(entry);
        Persist(entry);
    }

    private void StopTicking()
    {
        _ticker.Stop();
        IsRunning = false;
        if (Current is not null) Current.IsRunning = false;
    }

    /// <summary>Copies the timer's progress onto the entry it is loaded with.</summary>
    private void ApplyTimer(EntryViewModel entry)
    {
        entry.ElapsedSeconds = _timer.ElapsedSeconds;
        entry.Completed = _timer.Completed;
        _progress[entry.Key] = entry.State;
    }

    private void Persist(EntryViewModel entry)
    {
        _progress[entry.Key] = entry.State;
        _logStore.Save(entry.Key, entry.State);
    }

    private void RefreshTimer()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(TimerTitle));
        OnPropertyChanged(nameof(TimerSubtitle));
        OnPropertyChanged(nameof(TimerRemaining));
        OnPropertyChanged(nameof(TimerProgress));
        OnPropertyChanged(nameof(StartLabel));
        OnPropertyChanged(nameof(DayRemaining));
        StartCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
    }
}
