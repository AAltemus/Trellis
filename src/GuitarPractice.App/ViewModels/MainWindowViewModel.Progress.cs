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
/// "Log for this day" strip under the timer: BPM reached, 1–5 rating and notes for the selected item's exercise on the
/// selected item's date, plus a one-line progress summary. Stored by <see cref="Infrastructure.ProgressStore"/>.
/// </summary>
public sealed partial class MainWindowViewModel
{
    [ObservableProperty]
    private decimal? _logBpm;

    [ObservableProperty]
    private RatingOption? _logRating = RatingOption.All[0];

    [ObservableProperty]
    private string _logNotes = "";

    [ObservableProperty]
    private string _logTitle = "";

    [ObservableProperty]
    private string _logBpmWatermark = "BPM";

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
        LogBpm = saved?.BpmReached;
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
        var summary = ProgressStats.Summarize(_progressStore.History(entry.Key.SubcategoryId), exercise?.StartBpm, exercise?.TargetBpm, Today);
        LogBpmWatermark = summary.LatestBpm is int last ? $"last {last}" : exercise?.StartBpm is int start ? $"start {start}" : "BPM";
        ProgressHint = FormatHint(summary, exercise?.TargetBpm);
    }

    internal static string FormatHint(ProgressSummary s, int? targetBpm)
    {
        var parts = new List<string>();
        if (s.BestBpm is int best)
            parts.Add(targetBpm is int target && s.TargetFraction is double f
                ? $"best {best} BPM of {target} target ({f:P0})"
                : $"best {best} BPM");
        else if (targetBpm is int target) parts.Add($"target {target} BPM");
        if (s.RecentRating is double r) parts.Add($"rating {r:0.0}/5");
        if (s.Streak > 1) parts.Add($"{s.Streak}-day streak");
        if (s.MinutesLast28Days > 0) parts.Add($"{s.MinutesLast28Days} min in 4 weeks");
        return parts.Count > 0 ? string.Join(" · ", parts) : "No history yet: log a BPM or rating after you practise.";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void SaveLog()
    {
        if (Current is not { } entry) return;
        _progressStore.Save(entry.Key.Date, entry.Key.SubcategoryId, LogBpm is { } bpm ? (int)Math.Round(bpm) : null, LogRating?.Value, LogNotes);
        LogStatus = "Saved ✓";
        RefreshProgressHint();
    }

    [RelayCommand]
    private void OpenProgress() => _tools.ShowProgress(Current?.Key.SubcategoryId);
}
