using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GuitarPractice.Domain.Planning;
using GuitarPractice.Infrastructure;

namespace GuitarPractice.App.ViewModels;

/// <summary>A "Swap with…" menu entry: another item on the board, or a disabled line saying why not.</summary>
public sealed record SwapTargetViewModel(string Header, bool IsEnabled, ICommand? Command);

/// <summary>
/// Swapping two board items (drag one onto another, or right-click → Swap with…): the exercises trade places and each
/// slot keeps its day, position and minutes (<see cref="PlanSwap"/>). An unlocked week locks itself on the first swap,
/// since unlocked weeks are regenerated from the routine. Items with practice time or a progress log on their day,
/// warmups and the item in a running timer stay put.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>One line next to the week title: what the last swap did, or why it didn't happen.</summary>
    [ObservableProperty]
    private string? _boardMessage;

    /// <summary>Null when <paramref name="a"/> and <paramref name="b"/> can trade places, else why not.</summary>
    public string? WhyNotSwap(EntryViewModel a, EntryViewModel b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Key == b.Key) return "That's the same item.";
        return WhyNotMove(a) ?? WhyNotMove(b)
            ?? (a.Day != b.Day && b.Day.Entries.Any(e => e.Key.SubcategoryId == a.Key.SubcategoryId)
                ? $"{a.Name} is already on {DayName(b)}."
                : a.Day != b.Day && a.Day.Entries.Any(e => e.Key.SubcategoryId == b.Key.SubcategoryId)
                    ? $"{b.Name} is already on {DayName(a)}."
                    : null);
    }

    private string? WhyNotMove(EntryViewModel entry)
    {
        if (entry.IsPinned) return $"{entry.Category} stays at the start of each day.";
        if (IsRunning && ReferenceEquals(entry, Current)) return $"{entry.Name} is running in the timer: pause or stop it first.";
        if (HasPracticeLogged(entry))
            return $"{entry.Name} already has practice logged on {DayName(entry)}, so it stays there.";
        return null;
    }

    /// <summary>Practice time, done, or a progress log on its day: such an item never moves off that day.</summary>
    private bool HasPracticeLogged(EntryViewModel entry) =>
        entry.ElapsedSeconds > 0 || entry.Completed || _progressStore.Get(entry.Key.Date, entry.Key.SubcategoryId) is not null;

    private static string DayName(EntryViewModel entry) => entry.Key.Date.DayOfWeek.ToString();

    public void Swap(EntryViewModel a, EntryViewModel b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (WhyNotSwap(a, b) is { } why)
        {
            BoardMessage = why;
            return;
        }

        var lockedNow = false;
        if (!IsLocked)
        {
            if (_lastPlan is null || _lastRequest is null) return;
            _weekStore.Lock(_lastPlan, _lastRequest, _routineId); // unlocked weeks regenerate: freeze it so the swap stays
            IsLocked = true;
            lockedNow = true;
        }

        var result = _weekStore.Swap(_weekStart, (a.Key.Date, a.Key.SubcategoryId), (b.Key.Date, b.Key.SubcategoryId));
        if (result == SwapRefusal.None)
        {
            // The selection follows the exercise to its new place.
            if (_selectedKey == a.Key) _selectedKey = new PracticeKey(b.Key.Date, a.Key.SubcategoryId);
            else if (_selectedKey == b.Key) _selectedKey = new PracticeKey(a.Key.Date, b.Key.SubcategoryId);
        }
        RefreshNextWeek();
        ShowLockedWeek();
        BoardMessage = result == SwapRefusal.None
            ? $"Swapped {a.Name} ({DayName(a)}) and {b.Name} ({DayName(b)})." + (lockedNow ? " The week is now locked so the swap stays." : "")
            : $"Couldn't swap those items ({result}).";
    }

    /// <summary>The right-click "Swap with…" list for <paramref name="source"/>.</summary>
    public IReadOnlyList<SwapTargetViewModel> SwapTargets(EntryViewModel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (WhyNotMove(source) is { } why) return [new SwapTargetViewModel(why, false, null)];
        var targets = Days.SelectMany(d => d.Entries)
            .Where(e => e.Key != source.Key && WhyNotSwap(source, e) is null)
            .Select(e => new SwapTargetViewModel($"{DayName(e)} · {e.Name} · {e.PlannedMinutes} min", true,
                new RelayCommand(() => Swap(source, e))))
            .ToList();
        return targets.Count > 0 ? targets : [new SwapTargetViewModel("No other item can swap with this one", false, null)];
    }
}
