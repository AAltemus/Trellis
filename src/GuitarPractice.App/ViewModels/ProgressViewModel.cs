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
    public int CategoryId { get; } = subcategory.CategoryId;
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
/// <param name="others">The block's other current exercises by <see cref="ProgressStats.ExerciseKey"/> → label: typing
/// one of their names makes Rename a merge.</param>
public sealed partial class ExerciseSummaryRow(
    ExerciseProgress progress, string detail, int? blockTarget, Action<ExerciseSummaryRow> remove,
    Action<ExerciseSummaryRow, string> rename, IReadOnlyDictionary<string, string> others)
    : ObservableObject
{
    // ---- Rename / merge (inline) ----

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MergeInto), nameof(IsMerge), nameof(RenameButtonText), nameof(RenameHint))]
    [NotifyCanExecuteChangedFor(nameof(ApplyRenameCommand))]
    private string _renameText = "";

    /// <summary>The existing exercise the new name belongs to (same note value), if any.</summary>
    public string? MergeInto => others.GetValueOrDefault(ProgressStats.ExerciseKey(RenameText, NoteValue));

    public bool IsMerge => MergeInto is not null;

    public string RenameButtonText => IsMerge ? "Merge" : "Rename";

    public string RenameHint => MergeInto is { } into
        ? $"“{into}” already exists: this exercise's history joins it (its own target is kept)."
        : "Renames every logged line of this exercise; its own target goes with it.";

    [RelayCommand]
    private void StartRename()
    {
        RenameText = progress.Name;
        IsConfirmingRemove = false;
        IsRenaming = true;
    }

    private bool CanApplyRename() =>
        !string.IsNullOrWhiteSpace(RenameText) && RenameText.Trim() != progress.Name;

    [RelayCommand(CanExecute = nameof(CanApplyRename))]
    private void ApplyRename()
    {
        IsRenaming = false;
        rename(this, RenameText);
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

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

/// <summary>A category in "What's tracked": its checkbox and its blocks' checkboxes (disabled while the category is out).</summary>
public sealed partial class TrackedCategoryViewModel : ObservableObject
{
    private readonly ProgressTracking _tracking;
    private bool _reloading;

    public TrackedCategoryViewModel(ProgressTracking tracking, Category category)
    {
        _tracking = tracking;
        Id = category.Id;
        Name = category.Name;
        Blocks = [.. category.Subcategories.Select(s => new TrackedBlockViewModel(tracking, this, s.Id, s.DisplayName))];
        Reload();
    }

    public int Id { get; }
    public string Name { get; }
    public IReadOnlyList<TrackedBlockViewModel> Blocks { get; }

    [ObservableProperty]
    private bool _isTracked;

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsTrackedChanged(bool value)
    {
        if (!_reloading) _tracking.SetCategory(Id, value);
    }

    internal void Reload()
    {
        _reloading = true;
        var s = _tracking.Load();
        IsTracked = !(s.ExcludedCategories?.Contains(Id) ?? false);
        foreach (var b in Blocks) b.Reload(s);
        _reloading = false;
    }
}

public sealed partial class TrackedBlockViewModel(ProgressTracking tracking, TrackedCategoryViewModel category, int id, string name) : ObservableObject
{
    private bool _reloading;

    public int Id { get; } = id;
    public string Name { get; } = name;
    public TrackedCategoryViewModel Category { get; } = category;

    [ObservableProperty]
    private bool _isTracked = true;

    partial void OnIsTrackedChanged(bool value)
    {
        if (!_reloading) tracking.SetBlock(Id, value);
    }

    internal void Reload(ProgressTrackingSettings s)
    {
        _reloading = true;
        IsTracked = !(s.ExcludedBlocks?.Contains(Id) ?? false);
        _reloading = false;
    }
}

/// <summary>A Recorder take attached to the edited day: name, exercise, Open (in the Recorder).</summary>
public sealed partial class DayTakeViewModel(string fileName, string? exercise, Action<string> open)
{
    public string Name { get; } = Path.GetFileNameWithoutExtension(fileName);
    public string Detail { get; } = exercise ?? "";

    [RelayCommand]
    private void Open() => open(fileName);
}

/// <summary>An exercise run removed from the block: its history, read-only.</summary>
public sealed record RemovedExerciseRow(string Label, string Detail);

/// <summary>A day in the history table.</summary>
public sealed record DayRowViewModel(DateOnly Date, int Seconds, IReadOnlyList<ExerciseLog> Exercises, int? Rating, string? Notes, int Takes = 0)
{
    public string DateText => Date.ToString("ddd MMM d, yyyy", CultureInfo.InvariantCulture);
    public string TimeText => Seconds > 0 ? PracticeProgress.Format(Seconds) : "–";
    public string ExercisesText => Exercises.Count == 0 ? "–" : string.Join("; ", Exercises.Select(Describe));
    public string RatingText => Rating is int r ? $"{r}/5" : "–";
    public string NotesText => Notes ?? "";
    public string TakesText => Takes > 0 ? $"🎙 {Takes}" : "";

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

    public ProgressViewModel(LibraryStore library, ProgressStore store, TimeProvider clock, ProgressTracking tracking,
        TakeLinkStore takeLinks, IToolWindows tools)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(tracking);
        ArgumentNullException.ThrowIfNull(takeLinks);
        ArgumentNullException.ThrowIfNull(tools);
        _takeLinks = takeLinks;
        _tools = tools;
        _takeLinks.Changed += OnTakesChanged;
        _library = library;
        _store = store;
        _clock = clock;
        _tracking = tracking;
        foreach (var category in library.LoadLibrary())
            TrackedCategories.Add(new TrackedCategoryViewModel(tracking, category));
        _tracking.Changed += OnTrackingChanged;
        _inRoutine = library.GetOrCreateDefaultRoutine().Items.Where(i => i.Weight > 0).Select(i => i.SubcategoryId).ToHashSet();
        foreach (var category in library.LoadLibrary())
            foreach (var s in category.Subcategories)
                _allRows.Add(new ExerciseRowViewModel(s, category.Name));
        foreach (var s in library.LoadAllRemoved()) // incl. items of removed categories; shown while they have history
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

    public IReadOnlyList<RatingOption> RatingOptions { get; } = RatingOption.All; // instance: compiled bindings

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

    partial void OnShowAllExercisesChanged(bool value) => RefreshKeepingSelection();

    private void RefreshKeepingSelection()
    {
        var selected = SelectedExercise;
        RefreshList();
        SelectedExercise = selected is not null && Exercises.Contains(selected) ? selected : Exercises.FirstOrDefault();
    }

    // ---- What's tracked ----

    private readonly ProgressTracking _tracking;

    /// <summary>Categories with their blocks: untick to leave them out of the tracker (history is kept).</summary>
    public ObservableCollection<TrackedCategoryViewModel> TrackedCategories { get; } = [];

    private void OnTrackingChanged(object? sender, EventArgs e)
    {
        foreach (var c in TrackedCategories) c.Reload();
        RefreshKeepingSelection();
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

    /// <summary>Renames an exercise, or merges it into another of the same note value (see ProgressStore.RenameExercise).</summary>
    private void RenameExercise(ExerciseSummaryRow row, string newName)
    {
        if (SelectedExercise is not { } block) return;
        var into = row.MergeInto;
        try
        {
            var lines = _store.RenameExercise(block.Id, row.Name, row.NoteValue, newName); // Changed reloads the view
            TargetStatus = lines == 0 ? ""
                : into is not null ? $"Merged {row.Label} into {into} ({lines} line{(lines == 1 ? "" : "s")})."
                : $"Renamed {row.Label} to “{newName.Trim()}” ({lines} line{(lines == 1 ? "" : "s")}).";
        }
        catch (ArgumentException ex)
        {
            TargetStatus = ex.Message;
        }
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
        var tracked = _tracking.Load();
        bool IsTracked(ExerciseRowViewModel r) =>
            !(tracked.ExcludedCategories?.Contains(r.CategoryId) ?? false) && !(tracked.ExcludedBlocks?.Contains(r.Id) ?? false);
        foreach (var row in _allRows.Where(IsTracked)
                     .Where(r => withHistory.Contains(r.Id) || (!r.IsRemovedFromLibrary && (ShowAllExercises || _inRoutine.Contains(r.Id)))))
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

        var current = s.Exercises.ToDictionary(e => ProgressStats.ExerciseKey(e.Name, e.NoteValue), e => e.Label);
        foreach (var e in s.Exercises)
        {
            var detail = new List<string>();
            if (e.BestBpm is int best) detail.Add($"best {best}");
            if (e.LatestBpm is int latest && latest != e.BestBpm) detail.Add($"latest {latest}");
            if (e.TargetFraction is double f) detail.Add($"{f:P0} of {e.TargetBpm}{(e.HasOwnTarget ? "" : " (block)")}");
            detail.Add($"logged {e.TimesLogged}×, last {e.LastDate:MMM d}");
            var self = ProgressStats.ExerciseKey(e.Name, e.NoteValue);
            var others = current.Where(kv => kv.Key != self).ToDictionary(kv => kv.Key, kv => kv.Value);
            ExerciseSummaries.Add(new ExerciseSummaryRow(e, string.Join(" · ", detail), block.TargetBpm, RemoveExercise, RenameExercise, others));
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

        // Days with practice, a log entry or Recorder takes (a take-only day still shows, to open its takes).
        var takeCounts = _takeLinks.CountsByDay(block.Id);
        var active = _history.Where(d => d.HasActivity).ToDictionary(d => d.Date);
        foreach (var date in active.Keys.Union(takeCounts.Keys).OrderByDescending(d => d))
        {
            var d = active.GetValueOrDefault(date);
            DayRows.Add(new DayRowViewModel(date, d?.SecondsPractised ?? 0, d?.Exercises ?? [], d?.Rating, d?.Notes, takeCounts.GetValueOrDefault(date)));
        }

        var status = EditStatus;
        EditDate = selectDate.ToDateTime(TimeOnly.MinValue);
        LoadEditor();
        if (keepStatus) EditStatus = status;
        SaveEntryCommand.NotifyCanExecuteChanged();
        ClearEntryCommand.NotifyCanExecuteChanged();
    }

    // ---- Recorder takes attached to the block on the edited day ----

    private readonly TakeLinkStore _takeLinks;
    private readonly IToolWindows _tools;

    public ObservableCollection<DayTakeViewModel> DayTakes { get; } = [];

    [ObservableProperty]
    private bool _hasDayTakes;

    private void LoadDayTakes(int blockId, DateOnly date)
    {
        DayTakes.Clear();
        foreach (var file in _takeLinks.For(blockId, date))
            DayTakes.Add(new DayTakeViewModel(file, _takeLinks.Get(file)?.Exercise, f => _tools.ShowRecorder(selectTake: f)));
        HasDayTakes = DayTakes.Count > 0;
    }

    private void OnTakesChanged(object? sender, EventArgs e)
    {
        if (SelectedExercise is not null)
            LoadExercise(selectDate: EditDate is { } d ? DateOnly.FromDateTime(d) : Today, keepStatus: true);
    }

    private void LoadEditor()
    {
        EditStatus = "";
        if (SelectedExercise is not { } block || EditDate is not { } date)
        {
            DayTakes.Clear();
            HasDayTakes = false;
            EditExercises.Load([]);
            EditRating = RatingOption.All[0];
            EditNotes = "";
            return;
        }
        var entry = _store.Get(DateOnly.FromDateTime(date), block.Id);
        LoadDayTakes(block.Id, DateOnly.FromDateTime(date));
        EditExercises.SetSuggestions(_store.ExerciseNames(block.Id));
        EditExercises.Load(entry?.Exercises.Select(x => new ExerciseLog(x.Name, x.Bpm, x.NoteValue, x.RemovedRun)) ?? []);
        EditRating = RatingOption.All.FirstOrDefault(r => r.Value == entry?.Rating) ?? RatingOption.All[0];
        EditNotes = entry?.Notes ?? "";
    }

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        _tracking.Changed -= OnTrackingChanged;
        _takeLinks.Changed -= OnTakesChanged;
    }
}
