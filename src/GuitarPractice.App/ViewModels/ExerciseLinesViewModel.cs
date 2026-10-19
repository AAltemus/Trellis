using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GuitarPractice.Domain;
using GuitarPractice.Domain.Practice;

namespace GuitarPractice.App.ViewModels;

/// <summary>Note-value picker entry; null = not given.</summary>
public sealed record NoteValueOption(NoteValue? Value, string Label)
{
    public static IReadOnlyList<NoteValueOption> All { get; } =
    [
        new(null, "—"),
        new(NoteValue.Quarters, "quarters"),
        new(NoteValue.Eighths, "8ths"),
        new(NoteValue.Triplets, "triplets"),
        new(NoteValue.Sixteenths, "16ths"),
        new(NoteValue.Quintuplets, "quintuplets"),
        new(NoteValue.Sextuplets, "sextuplets"),
        new(NoteValue.Septuplets, "septuplets"),
        new(NoteValue.ThirtySeconds, "32nds"),
    ];

    public static NoteValueOption For(NoteValue? value) => All.FirstOrDefault(o => o.Value == value) ?? All[0];

    public override string ToString() => Label;
}

/// <summary>One exercise row in the editor.</summary>
public sealed partial class ExerciseLineViewModel(ExerciseLinesViewModel owner) : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private decimal? _bpm;

    [ObservableProperty]
    private NoteValueOption? _noteValue = NoteValueOption.All[0];

    /// <summary>Set when this line belongs to an exercise run removed from progress (kept as history; saved as it was).</summary>
    public int? RemovedRun { get; init; }

    public bool IsRemoved => RemovedRun is not null;

    /// <summary>Exercise names used before in this block (shared with the owner).</summary>
    public IReadOnlyList<string> Suggestions => owner.Suggestions;

    public IReadOnlyList<NoteValueOption> NoteValues { get; } = NoteValueOption.All; // instance: compiled bindings

    internal void RefreshSuggestions() => OnPropertyChanged(nameof(Suggestions));

    [RelayCommand]
    private void Remove() => owner.Remove(this);
}

/// <summary>
/// The exercises played in one block: rows of name (free text with suggestions) + BPM + note value. Used by the
/// timer-panel log strip and the Progress window editor.
/// </summary>
public sealed partial class ExerciseLinesViewModel : ObservableObject
{
    public ObservableCollection<ExerciseLineViewModel> Lines { get; } = [];

    public IReadOnlyList<string> Suggestions { get; private set; } = [];

    public void SetSuggestions(IReadOnlyList<string> names)
    {
        Suggestions = names;
        foreach (var line in Lines) line.RefreshSuggestions();
    }

    /// <summary>Shows the given lines, or one empty row when there are none (ready to type).</summary>
    public void Load(IEnumerable<ExerciseLog> logs)
    {
        Lines.Clear();
        foreach (var log in logs)
            Lines.Add(new ExerciseLineViewModel(this) { Name = log.Name, Bpm = log.Bpm, NoteValue = NoteValueOption.For(log.NoteValue), RemovedRun = log.RemovedRun });
        if (Lines.Count == 0) Add();
    }

    public IReadOnlyList<ExerciseLog> ToLogs() =>
        Lines.Select(l => new ExerciseLog(l.Name ?? "", l.Bpm is { } b ? (int)Math.Round(b) : null, l.NoteValue?.Value, l.RemovedRun)).ToList();

    /// <summary>Adds a row; it inherits the previous row's note value (people usually stay in one subdivision).</summary>
    [RelayCommand]
    private void Add() =>
        Lines.Add(new ExerciseLineViewModel(this) { NoteValue = Lines.Count > 0 ? Lines[^1].NoteValue : NoteValueOption.All[0] });

    internal void Remove(ExerciseLineViewModel line)
    {
        Lines.Remove(line);
        if (Lines.Count == 0) Add();
    }
}
