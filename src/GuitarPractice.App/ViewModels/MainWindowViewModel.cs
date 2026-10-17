using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GuitarPractice.Audio;
using GuitarPractice.Domain;
using GuitarPractice.Domain.Planning;
using GuitarPractice.Infrastructure;

namespace GuitarPractice.App.ViewModels;

/// <summary>
/// Library + routine editor with a live week and a practice session runner (see MainWindowViewModel.Session.cs).
/// Every change is saved immediately and the plan regenerates (the planner is pure and takes milliseconds).
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly LibraryStore _store;
    private readonly PracticeLogStore _logStore;
    private readonly WeekStore _weekStore;
    private readonly INotificationSound _sound;
    private readonly TimeProvider _clock;
    private readonly IToolWindows _tools;
    private readonly int _routineId;
    private readonly bool _loading;
    private DateOnly _weekStart; // the displayed week (see MainWindowViewModel.Navigation.cs)
    private readonly Dictionary<PracticeKey, PracticeState> _progress;
    private int _seed;

    public ObservableCollection<CategoryViewModel> Categories { get; } = [];
    public ObservableCollection<DayViewModel> Days { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public IReadOnlyList<Tuning> Tunings { get; }

    /// <summary>Monday-first, like the week view.</summary>
    public ObservableCollection<DayBudgetViewModel> DayBudgets { get; } = [];

    /// <summary>Master slider: moving it sets every day to the same minutes.</summary>
    [ObservableProperty]
    private double _everyDayMinutes = 60;

    private bool _settingAllDays;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DailyTotalSummary))]
    private bool _pinnedTimeOnTopOfBudget;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCategoryCommand))]
    private string _newCategoryName = "";

    public MainWindowViewModel(
        LibraryStore store, PracticeLogStore logStore, WeekStore weekStore, INotificationSound sound, TimeProvider clock,
        IToolWindows tools)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logStore);
        ArgumentNullException.ThrowIfNull(weekStore);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(tools);
        _store = store;
        _logStore = logStore;
        _weekStore = weekStore;
        _sound = sound;
        _clock = clock;
        _tools = tools;
        _loading = true;

        _weekStart = CurrentWeekStart;
        _progress = logStore.Load(_weekStart, _weekStart.AddDays(6));
        InitializeSession();
        InitializeWeeks();

        Tunings = store.LoadTunings();
        var routine = store.GetOrCreateDefaultRoutine();
        _routineId = routine.Id;
        _seed = routine.PlanSeed == 0 ? 1 : routine.PlanSeed;
        var weights = routine.Items.ToDictionary(i => i.SubcategoryId, i => i.Weight);

        var stored = routine.DayBudgets.ToDictionary(b => b.Day, b => b.Minutes);
        foreach (var day in MondayFirst)
            DayBudgets.Add(new DayBudgetViewModel(this, day, stored.GetValueOrDefault(day, 60)));
        EveryDayMinutes = stored.Values.Where(m => m > 0).DefaultIfEmpty(60).Max();
        PinnedTimeOnTopOfBudget = routine.PinnedTimeOnTopOfBudget;

        foreach (var category in store.LoadLibrary())
            Categories.Add(new CategoryViewModel(this, category, weights));

        _loading = false;
        Regenerate();
    }

    private static readonly DayOfWeek[] MondayFirst =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    private Dictionary<DayOfWeek, int> BudgetsByDay => DayBudgets.ToDictionary(d => d.Day, d => d.RoundedMinutes);

    private IEnumerable<SubcategoryViewModel> AllSubcategories => Categories.SelectMany(c => c.Subcategories);

    private int PinnedPerDay => AllSubcategories.Where(s => s.IsPinned && s.IsIncluded).Sum(s => s.PinnedMinutes);

    /// <summary>"Week: 6 h 15 min over 6 days (warmup 5 min/day on top)". Rest days get no warmup either.</summary>
    public string DailyTotalSummary
    {
        get
        {
            var practiceDays = DayBudgets.Count(d => d.RoundedMinutes > 0);
            var total = DayBudgets.Sum(d => d.RoundedMinutes) + (PinnedTimeOnTopOfBudget ? PinnedPerDay * practiceDays : 0);
            var week = total >= 60 ? $"{total / 60} h {total % 60:00} min" : $"{total} min";
            var warmup = PinnedPerDay == 0 ? "no warmup"
                : PinnedTimeOnTopOfBudget ? $"warmup {PinnedPerDay} min/day on top" : $"warmup {PinnedPerDay} min/day included";
            return practiceDays == 0
                ? "Every day is a rest day: set some minutes to get a plan."
                : $"Week: {week} over {practiceDays} day{(practiceDays == 1 ? "" : "s")} ({warmup})";
        }
    }

    partial void OnEveryDayMinutesChanged(double value)
    {
        if (_loading) return;
        _settingAllDays = true;
        try
        {
            foreach (var day in DayBudgets) day.Minutes = value;
        }
        finally
        {
            _settingAllDays = false;
        }
        SettingsChanged();
    }

    internal void OnDayBudgetChanged(DayBudgetViewModel day)
    {
        if (_loading || _settingAllDays) return;
        SettingsChanged();
    }

    partial void OnPinnedTimeOnTopOfBudgetChanged(bool value) => SettingsChanged();

    private void SettingsChanged()
    {
        if (_loading) return;
        OnPropertyChanged(nameof(DailyTotalSummary));
        _store.SaveSettings(_routineId, BudgetsByDay, PinnedTimeOnTopOfBudget);
        Regenerate();
    }

    private bool CanReshuffle() => !IsLocked;

    [RelayCommand(CanExecute = nameof(CanReshuffle))]
    private void Reshuffle()
    {
        _seed = Random.Shared.Next(1, int.MaxValue);
        _store.SaveSeed(_routineId, _seed);
        Regenerate();
    }

    // ---- Tools ----

    [RelayCommand]
    private void OpenMetronome() => _tools.ShowMetronome();

    [RelayCommand]
    private void OpenTuner() => _tools.ShowTuner();

    [RelayCommand]
    private void OpenRecorder() => _tools.ShowRecorder();

    private bool CanAddCategory() => !string.IsNullOrWhiteSpace(NewCategoryName);

    [RelayCommand(CanExecute = nameof(CanAddCategory))]
    private void AddCategory()
    {
        var category = _store.AddCategory(NewCategoryName);
        var vm = new CategoryViewModel(this, category, new Dictionary<int, int>()) { IsExpanded = true };
        // Keep Songs/Improv/Composition ordering intact: insert by sort order.
        var index = Categories.TakeWhile(c => c.SortOrder <= category.SortOrder).Count();
        Categories.Insert(index, vm);
        NewCategoryName = "";
    }

    // ---- Called by child view models ----

    internal void OnWeightChanged(SubcategoryViewModel subcategory)
    {
        if (_loading) return;
        _store.SetWeight(_routineId, subcategory.Id, subcategory.WeightValue);
        subcategory.Owner.RefreshSummary();
        OnPropertyChanged(nameof(DailyTotalSummary));
        Regenerate();
    }

    internal SubcategoryViewModel AddSubcategory(CategoryViewModel category, string name, SongDetails? song)
    {
        var entity = _store.AddSubcategory(category.Id, name, song);
        var tuningName = song?.TuningId is int id ? Tunings.FirstOrDefault(t => t.Id == id)?.Name : null;
        var vm = new SubcategoryViewModel(this, category, entity, tuningName, weight: 0);
        category.Subcategories.Add(vm);
        vm.Weight = 5; // new items join the routine at a neutral weight; saves + regenerates
        return vm;
    }

    internal void Delete(SubcategoryViewModel subcategory)
    {
        _store.Archive(subcategory.Id);
        subcategory.Owner.Subcategories.Remove(subcategory);
        subcategory.Owner.RefreshSummary();
        OnPropertyChanged(nameof(DailyTotalSummary));
        Regenerate();
    }

    private void Regenerate()
    {
        if (_loading) return;
        if (IsLocked)
        {
            ShowLockedWeek();
            return;
        }

        _lastPlan = null;
        _lastRequest = null;
        var selected = AllSubcategories.Where(s => s.WeightValue > 0).ToList();
        var byId = selected.ToDictionary(s => s.Id);
        var request = new PlanRequest(
            _weekStart,
            BudgetsByDay,
            selected.Select(s => s.ToSpec()).ToList(),
            new PlannerOptions(PinnedTimeOnTopOfBudget: PinnedTimeOnTopOfBudget),
            _seed);

        Days.Clear();
        Warnings.Clear();

        var errors = WeeklyPlanner.Validate(request);
        if (errors.Count > 0)
        {
            foreach (var error in errors.Distinct()) Warnings.Add(error);
            RelinkSession();
            RefreshWeekCommands();
            return;
        }

        var plan = WeeklyPlanner.Generate(request);
        _lastPlan = plan;
        _lastRequest = request;
        foreach (var w in plan.Warnings)
        {
            Warnings.Add(w.TaskId is int id && byId.TryGetValue(id, out var s)
                ? w.Message.Replace($"Task {id}", s.DisplayName, StringComparison.Ordinal)
                : w.Message);
        }

        var today = Today;
        foreach (var day in plan.Days)
        {
            var badge = PinnedTimeOnTopOfBudget && day.PinnedMinutes > 0 ? $"+{day.PinnedMinutes} warmup" : null;
            var dayVm = new DayViewModel(this, day.Date, day.Date == today, day.IsRestDay, day.TotalMinutes, badge);
            foreach (var e in day.Entries)
            {
                var s = byId[e.TaskId];
                var key = new PracticeKey(day.Date, e.TaskId);
                dayVm.Entries.Add(new EntryViewModel(
                    this, dayVm, key, s.Owner.Name, s.DisplayName, e.Minutes, s.Owner.Brush, _progress.GetValueOrDefault(key)));
            }
            dayVm.RefreshRemaining();
            Days.Add(dayVm);
        }

        RelinkSession();
        RefreshWeekCommands();
    }
}

public sealed partial class CategoryViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;

    public CategoryViewModel(MainWindowViewModel owner, Category category, IReadOnlyDictionary<int, int> weights)
    {
        _owner = owner;
        Id = category.Id;
        Name = category.Name;
        SortOrder = category.SortOrder;
        IsSongs = category.Kind == CategoryKind.Songs;
        Brush = Avalonia.Media.Brush.Parse(category.ColorHex ?? "#888888");
        Tunings = owner.Tunings;
        NewSongTuning = Tunings.FirstOrDefault();

        foreach (var s in category.Subcategories)
            Subcategories.Add(new SubcategoryViewModel(owner, this, s, s.Tuning?.Name, weights.GetValueOrDefault(s.Id)));

        RefreshSummary();
        IsExpanded = IsSongs || Subcategories.Any(s => s.WeightValue > 0);
    }

    public int Id { get; }
    public string Name { get; }
    public int SortOrder { get; }
    public bool IsSongs { get; }
    public bool IsExercise => !IsSongs;
    public IBrush Brush { get; }
    public IReadOnlyList<Tuning> Tunings { get; }
    public IReadOnlyList<SongStatus> SongStatuses { get; } = Enum.GetValues<SongStatus>();
    public ObservableCollection<SubcategoryViewModel> Subcategories { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string _summary = "";

    // ---- Add subcategory (exercise categories) ----
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSubcategoryCommand))]
    private string _newSubcategoryName = "";

    // ---- Add song (Songs category) ----
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSongCommand))]
    private string _newSongTitle = "";

    [ObservableProperty]
    private string _newSongArtist = "";

    [ObservableProperty]
    private Tuning? _newSongTuning;

    [ObservableProperty]
    private SongStatus _newSongStatus = SongStatus.Learning;

    public void RefreshSummary()
    {
        var selected = Subcategories.Count(s => s.WeightValue > 0);
        Summary = selected == 0
            ? $"{Subcategories.Count} available"
            : $"{selected} of {Subcategories.Count} selected";
    }

    private bool CanAddSubcategory() => !string.IsNullOrWhiteSpace(NewSubcategoryName);

    [RelayCommand(CanExecute = nameof(CanAddSubcategory))]
    private void AddSubcategory()
    {
        _owner.AddSubcategory(this, NewSubcategoryName, song: null);
        NewSubcategoryName = "";
    }

    private bool CanAddSong() => !string.IsNullOrWhiteSpace(NewSongTitle);

    [RelayCommand(CanExecute = nameof(CanAddSong))]
    private void AddSong()
    {
        _owner.AddSubcategory(this, NewSongTitle, new SongDetails(NewSongArtist, NewSongTuning?.Id, NewSongStatus));
        NewSongTitle = "";
        NewSongArtist = "";
        NewSongStatus = SongStatus.Learning;
    }
}

public sealed partial class SubcategoryViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;
    private readonly Subcategory _entity;

    public SubcategoryViewModel(MainWindowViewModel main, CategoryViewModel owner, Subcategory entity, string? tuningName, int weight)
    {
        _main = main;
        _entity = entity;
        Owner = owner;
        _weight = weight; // backing field: initial load must not trigger a save
        Detail = BuildDetail(entity, tuningName);
    }

    public CategoryViewModel Owner { get; }
    public int Id => _entity.Id;
    public string DisplayName => _entity.DisplayName;
    public string? Notes => _entity.Notes;
    public string? Detail { get; }
    public bool IsPinned => _entity.Pin != PinMode.None;
    public bool CanDelete => !_entity.IsBuiltIn;
    public int PinnedMinutes => _entity.PinnedMinutes ?? _entity.MinSessionMinutes;
    public string PinnedLabel => $"Include · pinned first, {PinnedMinutes} min every practice day";
    public int WeightValue => (int)Math.Round(Weight);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIncluded))]
    [NotifyPropertyChangedFor(nameof(WeightValue))]
    private double _weight;

    /// <summary>Pinned items are on/off; stored as weight 1 (the planner ignores weight for pinned items).</summary>
    public bool IsIncluded
    {
        get => WeightValue > 0;
        set => Weight = value ? 1 : 0;
    }

    partial void OnWeightChanged(double value) => _main.OnWeightChanged(this);

    [RelayCommand]
    private void Delete() => _main.Delete(this);

    public PlanTaskSpec ToSpec() => new(
        _entity.Id, Owner.Id, Owner.SortOrder, WeightValue,
        _entity.MinSessionMinutes, _entity.MaxSessionMinutes, _entity.Pin, _entity.PinnedMinutes);

    private static string? BuildDetail(Subcategory s, string? tuningName)
    {
        var parts = new List<string>();
        if (tuningName is not null) parts.Add(tuningName);
        if (s.Status is SongStatus status) parts.Add(status.ToString());
        if (s.StartBpm is int start && s.TargetBpm is int target) parts.Add($"{start}→{target} bpm");
        else if (s.TargetBpm is int only) parts.Add($"target {only} bpm");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}
