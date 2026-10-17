using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GuitarPractice.App.Controls;
using GuitarPractice.Domain;
using GuitarPractice.Domain.Planning;
using GuitarPractice.Domain.Practice;
using GuitarPractice.Infrastructure;

namespace GuitarPractice.App.ViewModels;

/// <summary>An exercise in the Progress list.</summary>
public sealed partial class ExerciseRowViewModel(Subcategory subcategory, string category) : ObservableObject
{
    public int Id { get; } = subcategory.Id;
    public string Name { get; } = subcategory.DisplayName;
    public string Category { get; } = category;
    public int? StartBpm { get; } = subcategory.StartBpm;
    public int? TargetBpm { get; } = subcategory.TargetBpm;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private DateOnly? _lastActivity;
}

/// <summary>A day in the history table.</summary>
public sealed record DayRowViewModel(DateOnly Date, int Seconds, int? Bpm, int? Rating, string? Notes)
{
    public string DateText => Date.ToString("ddd MMM d, yyyy", CultureInfo.InvariantCulture);
    public string TimeText => Seconds > 0 ? PracticeProgress.Format(Seconds) : "–";
    public string BpmText => Bpm?.ToString(CultureInfo.InvariantCulture) ?? "–";
    public string RatingText => Rating is int r ? $"{r}/5" : "–";
    public string NotesText => Notes ?? "";
}

/// <summary>
/// Tools → Progress: per-exercise history (practice time from the timer + logged BPM, rating, notes), headline stats,
/// a BPM chart and a minutes chart (separate single-axis charts), and an editor for any day.
/// </summary>
public sealed partial class ProgressViewModel : ObservableObject, IDisposable
{
    public const int ChartDays = 90;

    private readonly LibraryStore _library;
    private readonly ProgressStore _store;
    private readonly TimeProvider _clock;
    private readonly List<ExerciseRowViewModel> _allRows = [];
    private readonly HashSet<int> _inRoutine;
    private IReadOnlyList<ProgressDay> _history = [];

    public ProgressViewModel(LibraryStore library, ProgressStore store, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        _library = library;
        _store = store;
        _clock = clock;
        _inRoutine = library.GetOrCreateDefaultRoutine().Items.Where(i => i.Weight > 0).Select(i => i.SubcategoryId).ToHashSet();
        foreach (var category in library.LoadLibrary())
            foreach (var s in category.Subcategories)
                _allRows.Add(new ExerciseRowViewModel(s, category.Name));
        _store.Changed += OnStoreChanged;
        RefreshList();
        SelectedExercise = Exercises.FirstOrDefault();
    }

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    // ---- List ----

    public ObservableCollection<ExerciseRowViewModel> Exercises { get; } = [];

    [ObservableProperty]
    private bool _showAllExercises;

    [ObservableProperty]
    private ExerciseRowViewModel? _selectedExercise;

    // ---- Selected exercise ----

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _bestText = "";

    [ObservableProperty]
    private string _targetText = "";

    [ObservableProperty]
    private double _targetFraction;

    [ObservableProperty]
    private bool _hasTarget;

    [ObservableProperty]
    private string _streakText = "";

    [ObservableProperty]
    private string _minutesText = "";

    [ObservableProperty]
    private string _ratingText = "";

    [ObservableProperty]
    private IReadOnlyList<ChartPoint> _bpmPoints = [];

    [ObservableProperty]
    private IReadOnlyList<ChartPoint> _minutePoints = [];

    [ObservableProperty]
    private double? _targetBpm;

    [ObservableProperty]
    private string? _targetLabel;

    [ObservableProperty]
    private DateOnly _chartFrom;

    [ObservableProperty]
    private DateOnly _chartTo;

    public ObservableCollection<DayRowViewModel> DayRows { get; } = [];

    [ObservableProperty]
    private DayRowViewModel? _selectedDay;

    // ---- Editor ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEntryCommand), nameof(ClearEntryCommand))]
    private DateTime? _editDate;

    [ObservableProperty]
    private decimal? _editBpm;

    [ObservableProperty]
    private RatingOption? _editRating = RatingOption.All[0];

    [ObservableProperty]
    private string _editNotes = "";

    [ObservableProperty]
    private string _editStatus = "";

    public IReadOnlyList<RatingOption> RatingOptions => RatingOption.All;

    // ---- Behaviour ----

    /// <summary>Selects an exercise (e.g. the one selected in the timer), showing it even if it has no history yet.</summary>
    public void Select(int? subcategoryId)
    {
        if (subcategoryId is not int id) return;
        var row = _allRows.FirstOrDefault(r => r.Id == id);
        if (row is null) return;
        if (!Exercises.Contains(row)) Exercises.Insert(0, row);
        SelectedExercise = row;
    }

    partial void OnShowAllExercisesChanged(bool value)
    {
        var selected = SelectedExercise;
        RefreshList();
        SelectedExercise = selected is not null && Exercises.Contains(selected) ? selected : Exercises.FirstOrDefault();
    }

    partial void OnSelectedExerciseChanged(ExerciseRowViewModel? value) => LoadExercise(selectDate: Today);

    partial void OnSelectedDayChanged(DayRowViewModel? value)
    {
        if (value is not null) EditDate = value.Date.ToDateTime(TimeOnly.MinValue);
    }

    partial void OnEditDateChanged(DateTime? value) => LoadEditor();

    [RelayCommand]
    private void Refresh() => LoadExercise(selectDate: EditDate is { } d ? DateOnly.FromDateTime(d) : Today);

    private bool CanEdit() => SelectedExercise is not null && EditDate is not null;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void SaveEntry()
    {
        if (SelectedExercise is not { } exercise || EditDate is not { } date) return;
        _store.Save(DateOnly.FromDateTime(date), exercise.Id, EditBpm is { } b ? (int)Math.Round(b) : null, EditRating?.Value, EditNotes);
        EditStatus = "Saved ✓"; // the store's Changed event reloads the view
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void ClearEntry()
    {
        if (SelectedExercise is not { } exercise || EditDate is not { } date) return;
        _store.Save(DateOnly.FromDateTime(date), exercise.Id, null, null, null);
        EditStatus = "Cleared (practice time, if any, stays)";
    }

    private void OnStoreChanged(object? sender, int subcategoryId)
    {
        var row = _allRows.FirstOrDefault(r => r.Id == subcategoryId);
        if (row is not null) UpdateRowSummary(row);
        if (SelectedExercise?.Id == subcategoryId)
            LoadExercise(selectDate: EditDate is { } d ? DateOnly.FromDateTime(d) : Today, keepStatus: true);
    }

    private void RefreshList()
    {
        var withHistory = _store.SubcategoriesWithHistory();
        Exercises.Clear();
        foreach (var row in _allRows.Where(r => ShowAllExercises || _inRoutine.Contains(r.Id) || withHistory.Contains(r.Id)))
        {
            UpdateRowSummary(row);
            Exercises.Add(row);
        }
        // Most recently practised first; never-practised (routine items) after, in library order.
        var ordered = Exercises.OrderByDescending(r => r.LastActivity ?? DateOnly.MinValue).ToList();
        Exercises.Clear();
        foreach (var row in ordered) Exercises.Add(row);
    }

    private void UpdateRowSummary(ExerciseRowViewModel row)
    {
        var history = _store.History(row.Id);
        var s = ProgressStats.Summarize(history, row.StartBpm, row.TargetBpm, Today);
        row.LastActivity = history.Where(d => d.HasActivity && d.Date <= Today).Select(d => (DateOnly?)d.Date).LastOrDefault();
        var parts = new List<string>();
        if (row.LastActivity is DateOnly last) parts.Add(last == Today ? "today" : $"{(Today.DayNumber - last.DayNumber)} d ago");
        if (s.BestBpm is int best) parts.Add($"best {best}");
        if (s.MinutesLast28Days > 0) parts.Add($"{s.MinutesLast28Days} min/4 wk");
        row.Summary = parts.Count > 0 ? string.Join(" · ", parts) : "no history yet";
    }

    private void LoadExercise(DateOnly selectDate, bool keepStatus = false)
    {
        DayRows.Clear();
        ChartTo = Today;
        ChartFrom = Today.AddDays(-(ChartDays - 1));
        if (SelectedExercise is not { } exercise)
        {
            _history = [];
            Title = "Pick an exercise";
            BpmPoints = [];
            MinutePoints = [];
            EditDate = null;
            return;
        }

        _history = _store.History(exercise.Id);
        // Chart window: from a few days before the first activity, at least 4 weeks and at most 90 days.
        var first = _history.Where(d => d.HasActivity).Select(d => (DateOnly?)d.Date).FirstOrDefault() ?? Today;
        var span = Math.Clamp(Today.DayNumber - first.DayNumber + 4, 28, ChartDays);
        ChartFrom = Today.AddDays(-(span - 1));
        var s = ProgressStats.Summarize(_history, exercise.StartBpm, exercise.TargetBpm, Today);
        Title = $"{exercise.Name} · {exercise.Category}";
        BestText = s.BestBpm is int best ? $"{best} BPM" : "–";
        HasTarget = exercise.TargetBpm is not null;
        TargetFraction = s.TargetFraction ?? 0;
        TargetText = exercise.TargetBpm is int target
            ? $"target {target} BPM{(exercise.StartBpm is int start ? $" (from {start})" : "")}{(s.TargetFraction is double f ? $" · {f:P0} there" : "")}"
            : "no target BPM";
        StreakText = s.Streak > 0 ? $"{s.Streak} day{(s.Streak == 1 ? "" : "s")}" : "–";
        MinutesText = $"{s.MinutesThisWeek} min this week · {s.MinutesLast28Days} min in 4 weeks · {s.DaysPractised} days total";
        RatingText = s.RecentRating is double r ? $"{r:0.0}/5 (last {Math.Min(ProgressStats.RecentRatings, _history.Count(d => d.Rating is not null))})" : "–";

        TargetBpm = exercise.TargetBpm;
        TargetLabel = exercise.TargetBpm is int t ? $"target {t}" : null;
        BpmPoints = _history.Where(d => d.Bpm is not null)
            .Select(d => new ChartPoint(d.Date, d.Bpm!.Value, $"{d.Date:ddd MMM d}: {d.Bpm} BPM{(d.Rating is int rr ? $" · {rr}/5" : "")}"))
            .ToList();
        MinutePoints = _history.Where(d => d.SecondsPractised > 0)
            .Select(d => new ChartPoint(d.Date, d.SecondsPractised / 60.0, $"{d.Date:ddd MMM d}: {PracticeProgress.Format(d.SecondsPractised)} practised"))
            .ToList();

        foreach (var d in _history.Where(d => d.HasActivity).OrderByDescending(d => d.Date))
            DayRows.Add(new DayRowViewModel(d.Date, d.SecondsPractised, d.Bpm, d.Rating, d.Notes));

        var status = EditStatus;
        EditDate = selectDate.ToDateTime(TimeOnly.MinValue);
        LoadEditor();
        if (keepStatus) EditStatus = status;
        SaveEntryCommand.NotifyCanExecuteChanged();
        ClearEntryCommand.NotifyCanExecuteChanged();
    }

    private void LoadEditor()
    {
        EditStatus = "";
        if (SelectedExercise is not { } exercise || EditDate is not { } date)
        {
            EditBpm = null;
            EditRating = RatingOption.All[0];
            EditNotes = "";
            return;
        }
        var entry = _store.Get(DateOnly.FromDateTime(date), exercise.Id);
        EditBpm = entry?.BpmReached;
        EditRating = RatingOption.All.FirstOrDefault(r => r.Value == entry?.Rating) ?? RatingOption.All[0];
        EditNotes = entry?.Notes ?? "";
    }

    public void Dispose() => _store.Changed -= OnStoreChanged;
}
