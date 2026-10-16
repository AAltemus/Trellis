using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GuitarPractice.Domain.Planning;
using GuitarPractice.Infrastructure;

namespace GuitarPractice.App.ViewModels;

/// <summary>
/// Week menu: lock the current week (freeze it against routine changes), save the displayed week under a name,
/// and load a saved week into the current week (which locks it).
/// </summary>
public sealed partial class MainWindowViewModel
{
    private PlanResult? _lastPlan;
    private PlanRequest? _lastRequest;

    public ObservableCollection<SavedWeekViewModel> SavedWeeks { get; } = [];

    public bool HasSavedWeeks => SavedWeeks.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockMenuHeader), nameof(LockButtonText), nameof(LockStatus), nameof(CanLoadIntoDisplayedWeek), nameof(LoadMenuHeader))]
    [NotifyCanExecuteChangedFor(nameof(ReshuffleCommand))]
    private bool _isLocked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockStatus))]
    private string? _lockedWeekName;

    [ObservableProperty]
    private bool _isSaveDialogOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmSaveCommand))]
    private string _saveWeekName = "";

    /// <summary>Name of the saved week queued (locked) for next week; null when next week will come from the routine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNextWeekLoaded), nameof(NextWeekStatus), nameof(ShowNextWeekStrip), nameof(CanLoadIntoNextWeek), nameof(LoadNextMenuHeader))]
    [NotifyCanExecuteChangedFor(nameof(ClearNextWeekCommand))]
    private string? _nextWeekName;

    public bool HasNextWeekLoaded => NextWeekName is not null;

    /// <summary>The blue strip shows on the current week only; when viewing next week the lock banner says it.</summary>
    public bool ShowNextWeekStrip => HasNextWeekLoaded && IsViewingCurrentWeek;

    public string NextWeekStatus => $"Next week ({NextWeekStart:MMM d}) is queued from “{NextWeekName}”.";

    /// <summary>The real next week (relative to today, not to the displayed week).</summary>
    private DateOnly NextWeekStart => CurrentWeekStart.AddDays(7);

    // Overwrite protection: a locked week never takes a load; unlock it first (WeekStore.Load enforces it too).
    public bool CanLoadIntoDisplayedWeek => HasSavedWeeks && !IsLocked;

    public bool CanLoadIntoNextWeek => HasSavedWeeks && !HasNextWeekLoaded;

    public string LoadMenuHeader => IsLocked ? "L_oad saved week (unlock this week first)" : "L_oad saved week";

    public string LoadNextMenuHeader => HasNextWeekLoaded
        ? "Load saved week into _next week (next week is locked)"
        : "Load saved week into _next week";

    public string LockMenuHeader => IsLocked ? "_Unlock week" : "_Lock week";

    public string LockButtonText => IsLocked ? "🔓 Unlock week" : "🔒 Lock week";

    private string DisplayedWeekName => IsViewingCurrentWeek ? "This week" : $"The week of {_weekStart:MMM d}";

    public string LockStatus => LockedWeekName is { } name
        ? $"🔒 {DisplayedWeekName} is locked · loaded from “{name}”"
        : $"🔒 {DisplayedWeekName} is locked";

    private void InitializeWeeks()
    {
        IsLocked = _weekStore.LoadLocked(_weekStart) is not null;
        RefreshNextWeek();
        RefreshSavedWeeks();
    }

    private void RefreshNextWeek() =>
        NextWeekName = _weekStore.LoadLocked(NextWeekStart) is { } plan ? plan.Name ?? "a locked plan" : null;

    // ---- Lock ----

    private bool CanToggleLock() => IsLocked || (_lastPlan is not null && _lastRequest is not null);

    [RelayCommand(CanExecute = nameof(CanToggleLock))]
    private void ToggleLock()
    {
        if (IsLocked)
        {
            _weekStore.Unlock(_weekStart);
            LockedWeekName = null;
            IsLocked = false;
        }
        else
        {
            _weekStore.Lock(_lastPlan!, _lastRequest!, _routineId);
            IsLocked = true;
        }
        RefreshNextWeek();
        Regenerate();
    }

    private void ShowLockedWeek()
    {
        var plan = _weekStore.LoadLocked(_weekStart);
        if (plan is null)
        {
            IsLocked = false; // unlocked elsewhere (or the week rolled over): fall back to the live plan
            Regenerate();
            return;
        }

        LockedWeekName = plan.Name;
        Days.Clear();
        Warnings.Clear();

        var today = Today;
        foreach (var d in plan.Days)
        {
            var badge = d.TotalMinutes > d.BudgetMinutes && d.PinnedMinutes > 0 ? $"+{d.PinnedMinutes} warmup" : null;
            var dayVm = new DayViewModel(this, d.Date, d.Date == today, d.BudgetMinutes == 0, d.TotalMinutes, badge);
            foreach (var e in d.Entries)
            {
                var key = new PracticeKey(d.Date, e.SubcategoryId);
                var category = e.Subcategory.Category;
                dayVm.Entries.Add(new EntryViewModel(
                    this, dayVm, key, category.Name, e.Subcategory.DisplayName, e.PlannedMinutes,
                    Avalonia.Media.Brush.Parse(category.ColorHex ?? "#888888"), _progress.GetValueOrDefault(key)));
            }
            dayVm.RefreshRemaining();
            Days.Add(dayVm);
        }

        RelinkSession();
        RefreshWeekCommands();
    }

    // ---- Save ----

    private bool CanSaveWeek() => IsLocked || _lastPlan is not null;

    [RelayCommand(CanExecute = nameof(CanSaveWeek))]
    private void OpenSaveDialog()
    {
        SaveWeekName = LockedWeekName ?? $"Week of {_weekStart:MMM d}";
        IsSaveDialogOpen = true;
    }

    private bool CanConfirmSave() => !string.IsNullOrWhiteSpace(SaveWeekName);

    [RelayCommand(CanExecute = nameof(CanConfirmSave))]
    private void ConfirmSave()
    {
        if (IsLocked) _weekStore.SaveLocked(SaveWeekName, _weekStart);
        else if (_lastPlan is not null && _lastRequest is not null) _weekStore.Save(SaveWeekName, _lastPlan, _lastRequest, _routineId);
        IsSaveDialogOpen = false;
        RefreshSavedWeeks();
    }

    [RelayCommand]
    private void CancelSave() => IsSaveDialogOpen = false;

    // ---- Load / delete (called from SavedWeekViewModel) ----

    /// <summary>Loads a saved week into the displayed week. The week starts fresh: its logged times are cleared.</summary>
    internal void LoadSavedWeek(SavedWeekViewModel week)
    {
        if (IsLocked) return;
        if (IsRunning && Current is not null) PauseAndSave(Current);
        _weekStore.Load(week.Id, _weekStart);
        ReloadProgress();
        IsLocked = true;
        RefreshNextWeek();
        Regenerate(); // relinks the selection to a fresh entry, so the timer reloads at 0
    }

    internal void LoadSavedWeekIntoNext(SavedWeekViewModel week)
    {
        if (HasNextWeekLoaded) return;
        var viewingNext = _weekStart == NextWeekStart;
        if (viewingNext && IsRunning && Current is not null) PauseAndSave(Current);
        _weekStore.Load(week.Id, NextWeekStart);
        RefreshNextWeek();
        if (viewingNext)
        {
            ReloadProgress();
            ReloadDisplayedLock();
        }
    }

    [RelayCommand(CanExecute = nameof(HasNextWeekLoaded))]
    private void ClearNextWeek()
    {
        _weekStore.Unlock(NextWeekStart);
        RefreshNextWeek();
        if (_weekStart == NextWeekStart) ReloadDisplayedLock();
    }

    /// <summary>The displayed week was locked/unlocked behind the view's back (via the next-week menu).</summary>
    private void ReloadDisplayedLock()
    {
        LockedWeekName = null;
        IsLocked = _weekStore.LoadLocked(_weekStart) is not null;
        Regenerate();
    }

    internal void DeleteSavedWeek(SavedWeekViewModel week)
    {
        _weekStore.DeleteSaved(week.Id);
        RefreshSavedWeeks();
    }

    private void RefreshSavedWeeks()
    {
        SavedWeeks.Clear();
        foreach (var info in _weekStore.ListSaved())
            SavedWeeks.Add(new SavedWeekViewModel(this, info));
        OnPropertyChanged(nameof(HasSavedWeeks));
        OnPropertyChanged(nameof(CanLoadIntoDisplayedWeek));
        OnPropertyChanged(nameof(CanLoadIntoNextWeek));
    }

    private void RefreshWeekCommands()
    {
        ToggleLockCommand.NotifyCanExecuteChanged();
        OpenSaveDialogCommand.NotifyCanExecuteChanged();
    }

    // ---- File ----

    [RelayCommand]
    private void Exit()
    {
        SaveOnExit();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}

public sealed partial class SavedWeekViewModel(MainWindowViewModel main, SavedWeekInfo info) : ObservableObject
{
    public int Id => info.Id;
    public string Name => info.Name;

    /// <summary>Menu label: "Shred block — 6 days · 7 h 30 min".</summary>
    public string Label
    {
        get
        {
            var hours = info.TotalMinutes / 60;
            var minutes = info.TotalMinutes % 60;
            var total = hours > 0 ? $"{hours} h {minutes:00} min" : $"{minutes} min";
            return $"{info.Name} — {info.PracticeDays} days · {total}";
        }
    }

    [RelayCommand]
    private void Load() => main.LoadSavedWeek(this);

    [RelayCommand]
    private void LoadNext() => main.LoadSavedWeekIntoNext(this);

    [RelayCommand]
    private void Delete() => main.DeleteSavedWeek(this);
}
