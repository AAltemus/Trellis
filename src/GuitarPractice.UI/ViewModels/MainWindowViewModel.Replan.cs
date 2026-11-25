using CommunityToolkit.Mvvm.Input;
using GuitarPractice.Domain.Planning;

namespace GuitarPractice.App.ViewModels;

/// <summary>
/// Reshuffle from today (this week only): the days before today stay as they are; today to Sunday are planned again
/// from the routine so the whole week catches up to the weights (<see cref="WeeklyPlanner.Replan"/>), e.g. a song
/// added mid-week gets its share of the days left. Items already practised on those days (time, done or a progress
/// log) and the item in a running timer stay put. Works on a locked or unlocked week; the result is locked, since
/// unlocked weeks are regenerated from the routine.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private bool CanReshuffleFromToday() => IsViewingCurrentWeek && (IsLocked || _lastPlan is not null);

    [RelayCommand(CanExecute = nameof(CanReshuffleFromToday))]
    private void ReshuffleFromToday()
    {
        var today = Today;
        var current = IsLocked ? _weekStore.LoadLocked(_weekStart)?.ToPlannedDays() : _lastPlan?.Days;
        if (current is null) return;

        var keep = Days.Where(d => d.Date >= today).SelectMany(d => d.Entries)
            .Where(e => !e.IsPinned && (HasPracticeLogged(e) || (IsRunning && ReferenceEquals(e, Current))))
            .Select(e => (e.Key.Date, e.Key.SubcategoryId))
            .ToList();

        var request = BuildRequest(Random.Shared.Next(1, int.MaxValue));
        var errors = WeeklyPlanner.Validate(request);
        if (errors.Count > 0)
        {
            BoardMessage = errors[0];
            return;
        }

        var plan = WeeklyPlanner.Replan(request, current, today, keep);
        _weekStore.Lock(plan, request, _routineId);
        LockedWeekName = null;
        IsLocked = true;
        RefreshNextWeek();
        ShowLockedWeek();

        var names = AllSubcategories.ToDictionary(s => s.Id, s => s.DisplayName);
        foreach (var w in plan.Warnings)
            Warnings.Add(w.TaskId is int id && names.TryGetValue(id, out var name)
                ? w.Message.Replace($"Task {id}", name, StringComparison.Ordinal)
                : w.Message);

        var from = today == _weekStart ? "the whole week" : $"{today.DayOfWeek} to Sunday";
        BoardMessage = $"Reshuffled {from}; earlier days kept"
            + (keep.Count > 0 ? $", {keep.Count} practised item{(keep.Count == 1 ? "" : "s")} stayed in place" : "")
            + ". The week is locked so it stays.";
    }
}
