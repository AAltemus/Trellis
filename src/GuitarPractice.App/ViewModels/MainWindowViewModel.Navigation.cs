using CommunityToolkit.Mvvm.Input;
using GuitarPractice.Domain.Planning;

namespace GuitarPractice.App.ViewModels;

/// <summary>
/// ◀/▶ week navigation. The displayed week (<c>_weekStart</c>) can be any week; everything else (live plan, lock,
/// save, load, timer, logs) works on whichever week is shown. Unlocked weeks are generated from the routine with the
/// routine's seed, so previewing next week shows exactly the plan it will get unless it is locked or the routine changes.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    /// <summary>The real current week (recomputed each call, so it follows the clock across midnight/Monday).</summary>
    private DateOnly CurrentWeekStart => WeekMath.StartOfWeek(Today);

    /// <summary>Where the week board starts scrolled on open so today is in view (0 = left, 1 = right).</summary>
    public double TodayScrollFraction => WeekMath.BoardScrollFraction(Today.DayOfWeek);

    private int WeekOffset => (_weekStart.DayNumber - CurrentWeekStart.DayNumber) / 7;

    public bool IsViewingCurrentWeek => WeekOffset == 0;

    public string WeekTitle
    {
        get
        {
            var relative = WeekOffset switch
            {
                0 => "this week",
                1 => "next week",
                -1 => "last week",
                > 1 and var n => $"in {n} weeks",
                var n => $"{-n} weeks ago",
            };
            return $"Week of {_weekStart:ddd MMM d, yyyy} · {relative}";
        }
    }

    [RelayCommand]
    private void ShowPreviousWeek() => ShowWeek(_weekStart.AddDays(-7));

    [RelayCommand]
    private void ShowNextWeek() => ShowWeek(_weekStart.AddDays(7));

    [RelayCommand(CanExecute = nameof(CanShowThisWeek))]
    private void ShowThisWeek() => ShowWeek(CurrentWeekStart);

    private bool CanShowThisWeek() => !IsViewingCurrentWeek;

    private void ShowWeek(DateOnly weekStart)
    {
        if (weekStart == _weekStart) return;
        if (IsRunning && Current is not null) PauseAndSave(Current); // the running item is on the week we're leaving

        _weekStart = weekStart;
        ReloadProgress();

        LockedWeekName = null;
        IsLocked = _weekStore.LoadLocked(_weekStart) is not null;
        RefreshNextWeek();
        RefreshWeekHeader();
        Regenerate(); // locked → from the DB; unlocked → from the routine. The old selection doesn't match, so it clears.
    }

    /// <summary>Re-reads logged practice time for the displayed week.</summary>
    private void ReloadProgress()
    {
        _progress.Clear();
        foreach (var (key, state) in _logStore.Load(_weekStart, _weekStart.AddDays(6)))
            _progress[key] = state;
    }

    private void RefreshWeekHeader()
    {
        OnPropertyChanged(nameof(WeekTitle));
        OnPropertyChanged(nameof(IsViewingCurrentWeek));
        OnPropertyChanged(nameof(ShowNextWeekStrip));
        OnPropertyChanged(nameof(LockStatus));
        ShowThisWeekCommand.NotifyCanExecuteChanged();
    }
}
