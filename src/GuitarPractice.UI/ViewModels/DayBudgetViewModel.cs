using CommunityToolkit.Mvvm.ComponentModel;

namespace GuitarPractice.App.ViewModels;

/// <summary>Practice minutes for one weekday in the routine panel (0 = rest day).</summary>
public sealed partial class DayBudgetViewModel(MainWindowViewModel owner, DayOfWeek day, int minutes) : ObservableObject
{
    public const int MaxMinutes = 180;

    public DayOfWeek Day { get; } = day;

    /// <summary>"Mon".."Sun" (InvariantGlobalization is on, so names are English).</summary>
    public string Label { get; } = day.ToString()[..3];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinutesText), nameof(RoundedMinutes))]
    private double _minutes = minutes;

    /// <summary>Snapped to the planner's 5-minute quantum.</summary>
    public int RoundedMinutes => (int)(Math.Round(Minutes / 5) * 5);

    public string MinutesText => RoundedMinutes == 0 ? "Rest" : $"{RoundedMinutes} min";

    partial void OnMinutesChanged(double value) => owner.OnDayBudgetChanged(this);
}
