using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GuitarPractice.Domain.Planning;
using GuitarPractice.Infrastructure;

namespace GuitarPractice.App.ViewModels;

public sealed partial class DayViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;

    public DayViewModel(MainWindowViewModel main, DateOnly date, bool isToday, bool isRestDay, int totalMinutes, string? pinnedBadge)
    {
        _main = main;
        Date = date;
        IsToday = isToday;
        IsRestDay = isRestDay;
        TotalMinutes = totalMinutes;
        PinnedBadge = pinnedBadge;
    }

    public DateOnly Date { get; }
    public string Header => IsToday ? $"Today · {Date:ddd MMM d}" : $"{Date.DayOfWeek} {Date:MMM d}";
    public bool IsToday { get; }
    public bool IsRestDay { get; }
    public int TotalMinutes { get; }
    public string Total => IsRestDay ? "Rest" : $"{TotalMinutes} min";
    public string? PinnedBadge { get; }
    public ObservableCollection<EntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _remainingLabel = "";

    [ObservableProperty]
    private double _fraction;

    /// <summary>Recomputes the day's countdown from its entries; called every second while a timer runs.</summary>
    public void RefreshRemaining()
    {
        var remaining = Entries.Sum(e => e.RemainingSeconds);
        var total = Entries.Sum(e => e.PlannedMinutes * 60);
        RemainingLabel = IsRestDay || total == 0 ? ""
            : remaining == 0 ? "Done ✓"
            : remaining == total ? $"{PracticeProgress.Format(remaining)} to go"
            : $"{PracticeProgress.Format(remaining)} left";
        Fraction = total == 0 ? 0 : 1 - remaining / (double)total;
    }

    [RelayCommand]
    private void Select() => _main.SelectDay(this);
}

public sealed partial class EntryViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;

    public EntryViewModel(
        MainWindowViewModel main, DayViewModel day, PracticeKey key,
        string category, string name, int plannedMinutes, IBrush brush, PracticeState state, bool isPinned = false)
    {
        _main = main;
        Day = day;
        Key = key;
        Category = category;
        Name = name;
        PlannedMinutes = plannedMinutes;
        Brush = brush;
        IsPinned = isPinned;
        _elapsedSeconds = state.ElapsedSeconds;
        _completed = state.Completed;
    }

    public DayViewModel Day { get; }
    public PracticeKey Key { get; }
    public string Category { get; }
    public string Name { get; }
    public int PlannedMinutes { get; }
    public IBrush Brush { get; }

    /// <summary>A warmup pinned to the start of the day: stays put (no swapping).</summary>
    public bool IsPinned { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemainingSeconds), nameof(IsDone), nameof(Fraction), nameof(StatusLabel))]
    private int _elapsedSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemainingSeconds), nameof(IsDone), nameof(Fraction), nameof(StatusLabel))]
    private bool _completed;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isRunning;

    public int RemainingSeconds => PracticeProgress.RemainingSeconds(PlannedMinutes, ElapsedSeconds, Completed);
    public bool IsDone => PracticeProgress.IsDone(PlannedMinutes, ElapsedSeconds, Completed);
    public double Fraction => PracticeProgress.Fraction(PlannedMinutes, ElapsedSeconds, Completed);
    public PracticeState State => new(ElapsedSeconds, Completed);

    public string StatusLabel =>
        IsDone && ElapsedSeconds < PlannedMinutes * 60 ? $"✓ {PracticeProgress.Format(ElapsedSeconds)} of {PlannedMinutes} min"
        : IsDone ? $"✓ {PlannedMinutes} min"
        : ElapsedSeconds > 0 ? $"{PracticeProgress.Format(RemainingSeconds)} left of {PlannedMinutes} min"
        : $"{PlannedMinutes} min";

    [RelayCommand]
    private void Select() => _main.Select(this);
}
