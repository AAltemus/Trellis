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

/// <summary>
/// A block (subcategory) in the Progress list. <paramref name="removedFromLibrary"/>: the block was removed from the
/// library; it is listed only while it has history.
/// </summary>
public sealed partial class ExerciseRowViewModel(Subcategory subcategory, string category, bool removedFromLibrary = false) : ObservableObject
{
    public int Id { get; } = subcategory.Id;
    public string Name { get; } = subcategory.DisplayName;
    public string Category { get; } = removedFromLibrary ? $"{category} · removed from library" : category;
    public bool IsRemovedFromLibrary { get; } = removedFromLibrary;
    public int? StartBpm { get; } = subcategory.StartBpm;

    /// <summary>The block's target; editable in the Progress window.</summary>
    [ObservableProperty]
    private int? _targetBpm = subcategory.TargetBpm;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private DateOnly? _lastActivity;
}

/// <summary>
/// One exercise within the selected block, with its own (editable) target; empty = use the block target. Remove (asks
/// first) takes it out of current progress but keeps its history.
/// </summary>
public sealed partial class ExerciseSummaryRow(ExerciseProgress progress, string detail, int? blockTarget, Action<ExerciseSummaryRow> remove)
    : ObservableObject
{
    public string Name { get; } = progress.Name;
    public NoteValue? NoteValue { get; } = progress.NoteValue;
    public int Slot { get; } = progress.Slot;
    public bool IsCharted { get; } = progress.Slot < ProgressChart.MaxSeries;
    public string Label { get; } = progress.Label;
    public string Detail { get; } = detail;
    public double TargetFraction { get; } = progress.TargetFraction ?? 0;
    public bool HasTarget { get; } = progress.TargetFraction is not null;

    /// <summary>The value as loaded, to tell whether it changed.</summary>
    public int? OriginalTarget { get; } = progress.HasOwnTarget ? progress.TargetBpm : null;

    [ObservableProperty]
    private decimal? _ownTarget = progress.HasOwnTarget ? progress.TargetBpm : null;

    public string TargetWatermark { get; } = blockTarget is int b ? $"block {b}" : "target";

    [ObservableProperty]
    private bool _isConfirmingRemove;

    [RelayCommand]
    private void Remove()
    {
        if (!IsConfirmingRemove)
        {
            IsConfirmingRemove = true;
            return;
        }
        IsConfirmingRemove = false;
        remove(this);
    }

    [RelayCommand]
    private void CancelRemove() => IsConfirmingRemove = false;
}

/// <summary>An exercise run removed from the block: its history, read-only.</summary>
public sealed record RemovedExerciseRow(string Label, string Detail);

/// <summary>A day in the history table.</summary>
public sealed record DayRowViewModel(DateOnly Date, int Seconds, IReadOnlyList<ExerciseLog> Exercises, int? Rating, string? Notes)
{
    public string DateText => Date.ToString("ddd MMM d, yyyy", CultureInfo.InvariantCulture);
    public string TimeText => Seconds > 0 ? PracticeProgress.Format(Seconds) : "–";
    public string ExercisesText => Exercises.Count == 0 ? "–" : string.Join("; ", Exercises.Select(Describe));
    public string RatingText => Rating is int r ? $"{r}/5" : "–";
    public string NotesText => Notes ?? "";

    private static string Describe(ExerciseLog x)
    {
        var name = string.IsNullOrEmpty(x.Name) ? "(unnamed)" : x.Name;
        var tempo = x.Bpm is int b ? $" {b}" : "";
        var value = x.NoteValue is { } v ? $" {ProgressStats.NoteValueName(v)}" : "";
        return name + tempo + value + (x.RemovedRun is not null ? " (removed)" : "");
    }
}

/// <summary>
/// Tools → Progress: per-block history (practice time from the timer + the exercises played with BPM and note value,
/// rating, notes), per-exercise stats, a BPM chart with one line per exercise (the block's first four exercises, in
/// fixed colour slots), a minutes chart, and an editor for any day.
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
        foreach (var s in library.LoadRemoved()) // their history is kept; shown while they have some
            _allRows.Add(new ExerciseRowViewModel(s, s.Category.Name, removedFromLibrary: true));
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

    // ---- Selected block ----

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _targetText = "";

    /// <summary>Editable block target (empty = none).</summary>
    [ObservableProperty]
    private decimal? _blockTarget;

    [ObservableProperty]
    private string _targetStatus = "";

    [ObservableProperty]
    private string _streakText = "";

    [ObservableProperty]
    private string _minutesText = "";

    [ObservableProperty]
    private string _ratingText = "";

    public ObservableCollection<ExerciseSummaryRow> ExerciseSummaries { get; } = [];

    [ObservableProperty]
    private bool _hasUnchartedExercises;

    /// <summary>Exercises removed from this block, newest first; their lines stay in the day table.</summary>
    public ObservableCollection<RemovedExerciseRow> RemovedExercises { get; } = [];

    [ObservableProperty]
    private bool _hasRemovedExercises;

    [ObservableProperty]
    private IReadOnlyList<ChartSeries> _bpmSeries = [];

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

    public ExerciseLinesViewModel EditExercises { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEntryCommand), nameof(ClearEntryCommand))]
    private DateTime? _editDate;

    [ObservableProperty]
    private RatingOption? _editRating = RatingOption.All[0];

    [ObservableProperty]
    private string _editNotes = "";

    [ObservableProperty]
    private string _editStatus = "";

    public IReadOnlyList<RatingOption> RatingOptions => RatingOption.All;

    // ---- Behaviour ----

    /// <summary>Selects a block (e.g. the one selected on the board), showing it even if it has no history yet.</summary>
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

    /// <summary>Saves the block target and every exercise target that changed.</summary>
    [RelayCommand]
    private void SaveTargets()
    {
        if (SelectedExercise is not { } block) return;
        var blockTarget = BlockTarget is { } b ? (int)Math.Round(b) : (int?)null;
        if (blockTarget != block.TargetBpm)
        {
            _store.SetBlockTarget(block.Id, blockTarget);
            block.TargetBpm = blockTarget is >= 1 and <= 400 ? blockTarget : null;
        }
        foreach (var row in ExerciseSummaries.ToList())
        {
            var own = row.OwnTarget is { } o ? (int)Math.Round(o) : (int?)null;
            if (own != row.OriginalTarget) _store.SetTarget(block.Id, row.Name, row.NoteValue, own);
        }
        LoadExercise(selectDate: EditDate is { } d ? DateOnly.FromDateTime(d) : Today);
        TargetStatus = "Targets saved ✓";
    }

    [RelayCommand]
    private void Refresh() => LoadExercise(selectDate: EditDate is { } d ? DateOnly.FromDateTime(d) : Today);

    private bool CanEdit() => SelectedExercise is not null && EditDate is not null;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void SaveEntry()
    {
        if (SelectedExercise is not { } block || EditDate is not { } date) return;
        _store.Save(DateOnly.FromDateTime(date), block.Id, EditExercises.ToLogs(), EditRating?.Value, EditNotes);
        EditStatus = "Saved ✓"; // the store's Changed event reloads the view
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void ClearEntry()
    {
        if (SelectedExercise is not { } block || EditDate is not { } date) return;
        _store.Save(DateOnly.FromDateTime(date), block.Id, [], null, null);
        EditStatus = "Cleared (practice time, if any, stays)";
    }

    /// <summary>Takes an exercise out of the block's current progress; its history stays (see ProgressStore.RemoveExercise).</summary>
    private void RemoveExercise(ExerciseSummaryRow row)
    {
        if (SelectedExercise is not { } block) return;
        var lines = _store.RemoveExercise(block.Id, row.Name, row.NoteValue); // Changed reloads the view
        TargetStatus = lines > 0
            ? $"Removed {row.Label} ({lines} line{(lines == 1 ? "" : "s")} kept as history). Log the same name again to start over."
            : "";
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
        foreach (var row in _allRows.Where(r => withHistory.Contains(r.Id) || (!r.IsRemovedFromLibrary && (ShowAllExercises || _inRoutine.Contains(r.Id)))))
        {
            UpdateRowSummary(row);
            Exercises.Add(row);
        }
        var ordered = Exercises.OrderByDescending(r => r.LastActivity ?? DateOnly.MinValue).ToList();
        Exercises.Clear();
        foreach (var row in ordered) Exercises.Add(row);
    }

    private void UpdateRowSummary(ExerciseRowViewModel row)
    {
        var history = _store.History(row.Id);
        var s = ProgressStats.Summarize(history, row.StartBpm, row.TargetBpm, Today, _store.Targets(row.Id));
        row.LastActivity = history.Where(d => d.HasActivity && d.Date <= Today).Select(d => (DateOnly?)d.Date).LastOrDefault();
        var parts = new List<string>();
        if (row.LastActivity is DateOnly last) parts.Add(last == Today ? "today" : $"{(Today.DayNumber - last.DayNumber)} d ago");
        if (s.Exercises.Count > 0) parts.Add($"{s.Exercises.Count} exercise{(s.Exercises.Count == 1 ? "" : "s")}");
        if (s.MinutesLast28Days > 0) parts.Add($"{s.MinutesLast28Days} min/4 wk");
        row.Summary = parts.Count > 0 ? string.Join(" · ", parts) : "no history yet";
    }

    private void LoadExercise(DateOnly selectDate, bool keepStatus = false)
    {
        DayRows.Clear();
        ExerciseSummaries.Clear();
        ChartTo = Today;
        ChartFrom = Today.AddDays(-(ChartDays - 1));
        if (SelectedExercise is not { } block)
        {
            _history = [];
            Title = "Pick a block";
            BpmSeries = [];
            MinutePoints = [];
            EditDate = null;
            return;
        }

        _history = _store.History(block.Id);
        // Chart window: from a few days before the first activity, at least 4 weeks and at most 90 days.
        var first = _history.Where(d => d.HasActivity).Select(d => (DateOnly?)d.Date).FirstOrDefault() ?? Today;
        var span = Math.Clamp(Today.DayNumber - first.DayNumber + 4, 28, ChartDays);
        ChartFrom = Today.AddDays(-(span - 1));

        var s = ProgressStats.Summarize(_history, block.StartBpm, block.TargetBpm, Today, _store.Targets(block.Id));
        Title = $"{block.Name} · {block.Category}";
        BlockTarget = block.TargetBpm;
        TargetStatus = "";
        TargetText = block.StartBpm is int start ? $"BPM (start {start}); exercises without their own target use it" : "BPM; exercises without their own target use it";
        StreakText = s.Streak > 0 ? $"{s.Streak} day{(s.Streak == 1 ? "" : "s")}" : "–";
        MinutesText = $"{s.MinutesThisWeek} min this week · {s.MinutesLast28Days} min in 4 weeks · {s.DaysPractised} days total";
        RatingText = s.RecentRating is double r ? $"{r:0.0}/5 (last {Math.Min(ProgressStats.RecentRatings, _history.Count(d => d.Rating is not null))})" : "–";

        foreach (var e in s.Exercises)
        {
            var detail = new List<string>();
            if (e.BestBpm is int best) detail.Add($"best {best}");
            if (e.LatestBpm is int latest && latest != e.BestBpm) detail.Add($"latest {latest}");
            if (e.TargetFraction is double f) detail.Add($"{f:P0} of {e.TargetBpm}{(e.HasOwnTarget ? "" : " (block)")}");
            detail.Add($"logged {e.TimesLogged}×, last {e.LastDate:MMM d}");
            ExerciseSummaries.Add(new ExerciseSummaryRow(e, string.Join(" · ", detail), block.TargetBpm, RemoveExercise));
        }
        RemovedExercises.Clear();
        foreach (var e in s.Removed.OrderByDescending(e => e.LastDate))
        {
            var detail = new List<string>();
            if (e.BestBpm is int best) detail.Add($"best {best}");
            if (e.LatestBpm is int latest && latest != e.BestBpm) detail.Add($"last {latest}");
            detail.Add($"logged {e.TimesLogged}×");
            detail.Add(e.FirstDate == e.LastDate ? $"{e.FirstDate:MMM d, yyyy}" : $"{e.FirstDate:MMM d, yyyy} – {e.LastDate:MMM d, yyyy}");
            RemovedExercises.Add(new RemovedExerciseRow(e.Label, string.Join(" · ", detail)));
        }
        HasRemovedExercises = RemovedExercises.Count > 0;
        HasUnchartedExercises = s.Exercises.Count > ProgressChart.MaxSeries;

        // Block target line (neutral) only while some charted exercise still uses it; own targets are drawn per line.
        var usesBlock = s.Exercises.Any(e => e.Slot < ProgressChart.MaxSeries && !e.HasOwnTarget) || s.Exercises.Count == 0;
        TargetBpm = usesBlock ? block.TargetBpm : null;
        TargetLabel = usesBlock && block.TargetBpm is int t ? $"block target {t}" : null;
        // Colour follows the exercise: slot = order first logged in this block; the first four get lines.
        BpmSeries = s.Exercises
            .Where(e => e.Slot < ProgressChart.MaxSeries && e.BpmHistory.Count > 0)
            .Select(e => new ChartSeries(e.Label, e.Slot, e.BpmHistory
                .Select(h => new ChartPoint(h.Date, h.Bpm,
                    $"{e.Label}: {h.Bpm} BPM on {h.Date:ddd MMM d}{(e.TargetBpm is int tb ? $" (target {tb})" : "")}"))
                .ToList(), e.HasOwnTarget ? e.TargetBpm : null))
            .ToList();
        MinutePoints = _history.Where(d => d.SecondsPractised > 0)
            .Select(d => new ChartPoint(d.Date, d.SecondsPractised / 60.0, $"{d.Date:ddd MMM d}: {PracticeProgress.Format(d.SecondsPractised)} practised"))
            .ToList();

        foreach (var d in _history.Where(d => d.HasActivity).OrderByDescending(d => d.Date))
            DayRows.Add(new DayRowViewModel(d.Date, d.SecondsPractised, d.Exercises, d.Rating, d.Notes));

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
        if (SelectedExercise is not { } block || EditDate is not { } date)
        {
            EditExercises.Load([]);
            EditRating = RatingOption.All[0];
            EditNotes = "";
            return;
        }
        var entry = _store.Get(DateOnly.FromDateTime(date), block.Id);
        EditExercises.SetSuggestions(_store.ExerciseNames(block.Id));
        EditExercises.Load(entry?.Exercises.Select(x => new ExerciseLog(x.Name, x.Bpm, x.NoteValue, x.RemovedRun)) ?? []);
        EditRating = RatingOption.All.FirstOrDefault(r => r.Value == entry?.Rating) ?? RatingOption.All[0];
        EditNotes = entry?.Notes ?? "";
    }

    public void Dispose() => _store.Changed -= OnStoreChanged;
}
