using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GuitarPractice.Domain.Practice;

namespace GuitarPractice.App.ViewModels;

/// <summary>Rating picker entry; null = not rated.</summary>
public sealed record RatingOption(int? Value, string Label)
{
    public static IReadOnlyList<RatingOption> All { get; } =
    [
        new(null, "—"),
        new(1, "1 · rough"),
        new(2, "2"),
        new(3, "3 · ok"),
        new(4, "4"),
        new(5, "5 · clean"),
    ];

    public override string ToString() => Label;
}

/// <summary>
/// "Log for this day" strip under the timer: the exercises played (name, BPM, note value), a 1–5 rating and notes for the selected item's block on the
/// selected item's date, plus a one-line progress summary. Stored by <see cref="Infrastructure.ProgressStore"/>.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>The exercises played in the selected block on the selected day.</summary>
    public ExerciseLinesViewModel LogExercises { get; } = new();

    [ObservableProperty]
    private RatingOption? _logRating = RatingOption.All[0];

    [ObservableProperty]
    private string _logNotes = "";

    [ObservableProperty]
    private string _logTitle = "";

    [ObservableProperty]
    private string _progressHint = "";

    [ObservableProperty]
    private string _logStatus = "";

    public IReadOnlyList<RatingOption> RatingOptions => RatingOption.All;

    private Infrastructure.PracticeKey? _logKey; // the (day, exercise) the fields are showing

    private SubcategoryViewModel? CurrentExercise =>
        Current is { } entry ? AllSubcategories.FirstOrDefault(s => s.Id == entry.Key.SubcategoryId) : null;

    /// <summary>Called when the selected item changes: shows that day's entry and the exercise's progress.</summary>
    private void LoadProgressLog()
    {
        SaveLogCommand.NotifyCanExecuteChanged();
        OpenProgressCommand.NotifyCanExecuteChanged();
        if (Current is not { } entry)
        {
            _logKey = null;
            LogTitle = "";
            ProgressHint = "";
            LogStatus = "";
            return;
        }
        if (_logKey == entry.Key) return; // same item re-linked after a regenerate: keep what's being typed
        _logKey = entry.Key;
        _tools.FollowInProgress(entry.Key.SubcategoryId); // an open Progress window follows the board (one-way)
        LogStatus = "";

        var saved = _progressStore.Get(entry.Key.Date, entry.Key.SubcategoryId);
        LogExercises.SetSuggestions(_progressStore.ExerciseNames(entry.Key.SubcategoryId));
        LogExercises.Load(saved?.Exercises.Select(x => new ExerciseLog(x.Name, x.Bpm, x.NoteValue)) ?? []);
        LogRating = RatingOption.All.FirstOrDefault(r => r.Value == saved?.Rating) ?? RatingOption.All[0];
        LogNotes = saved?.Notes ?? "";
        LogTitle = $"Log for {entry.Key.Date:ddd MMM d}";
        RefreshProgressHint();
    }

    private void RefreshProgressHint()
    {
        if (Current is not { } entry)
        {
            ProgressHint = "";
            return;
        }
        var exercise = CurrentExercise;
        var summary = ProgressStats.Summarize(_progressStore.History(entry.Key.SubcategoryId), exercise?.StartBpm,
            _progressStore.BlockTarget(entry.Key.SubcategoryId), Today, _progressStore.Targets(entry.Key.SubcategoryId));
        ProgressHint = FormatHint(summary, _progressStore.BlockTarget(entry.Key.SubcategoryId));
    }

    internal static string FormatHint(ProgressSummary s, int? targetBpm)
    {
        var parts = new List<string>();
        // The two most recently logged exercises that have a tempo: "3nps scale · 16ths best 132".
        foreach (var e in s.Exercises.Where(e => e.BestBpm is not null).OrderByDescending(e => e.LastDate).ThenByDescending(e => e.Slot).Take(2))
            parts.Add($"{e.Label} best {e.BestBpm}{(e.TargetBpm is int t && e.TargetFraction is double f ? $" ({f:P0} of {t})" : "")}");
        if (parts.Count == 0 && targetBpm is int target) parts.Add($"target {target} BPM");
        if (s.RecentRating is double r) parts.Add($"rating {r:0.0}/5");
        if (s.Streak > 1) parts.Add($"{s.Streak}-day streak");
        if (s.MinutesLast28Days > 0) parts.Add($"{s.MinutesLast28Days} min in 4 weeks");
        return parts.Count > 0 ? string.Join(" · ", parts) : "No history yet: log what you played (and the BPM) after you practise.";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void SaveLog()
    {
        if (Current is not { } entry) return;
        _savingLog = true;
        try
        {
            _progressStore.Save(entry.Key.Date, entry.Key.SubcategoryId, LogExercises.ToLogs(), LogRating?.Value, LogNotes);
        }
        finally
        {
            _savingLog = false;
        }
        LogStatus = "Saved ✓";
        LogExercises.SetSuggestions(_progressStore.ExerciseNames(entry.Key.SubcategoryId));
        RefreshProgressHint();
    }

    private bool _savingLog;

    /// <summary>
    /// The same block was saved elsewhere (the Progress window). Reload the strip from the database so a later "Save log"
    /// here can't overwrite that edit with stale lines (the cause of a lost exercise line on 2026-10-17, most likely).
    /// </summary>
    private void OnProgressChangedElsewhere(int subcategoryId)
    {
        if (Current?.Key.SubcategoryId != subcategoryId) return;
        if (!_savingLog)
        {
            _logKey = null;      // force a reload of the fields
            LoadProgressLog();
            LogStatus = "Updated from the Progress window";
        }
        RefreshProgressHint();
    }

    [RelayCommand]
    private void OpenProgress() => _tools.ShowProgress(Current?.Key.SubcategoryId);
}
