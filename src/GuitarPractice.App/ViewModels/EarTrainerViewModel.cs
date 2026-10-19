using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GuitarPractice.Audio;
using GuitarPractice.Domain.EarTraining;
using GuitarPractice.Domain.Practice;
using GuitarPractice.Infrastructure;

namespace GuitarPractice.App.ViewModels;

/// <summary>Remembered between sessions (AppSettings key "earTrainer"). Fields are only appended.</summary>
public sealed record EarTrainerSettings(
    EarItemKind Mode = EarItemKind.Interval,
    int Tonic = 0,
    KeyMode KeyMode = KeyMode.Major,
    List<int>? Intervals = null,
    List<string>? Chords = null,
    IntervalDirection Direction = IntervalDirection.Ascending,
    IntervalRoot Root = IntervalRoot.Tonic,
    bool PlayKeyFirst = true,
    bool AutoNext = true,
    double Volume = 80,
    int MelodyLength = 4);

/// <summary>All-time results per <see cref="EarItem.StatsKey"/> (AppSettings key "earTrainerStats").</summary>
public sealed record EarTrainerStats(Dictionary<string, EarStat> Items);

public sealed record LabeledOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A checkbox: practise this interval / chord or not.</summary>
public sealed partial class EarChoiceViewModel(EarTrainerViewModel owner, EarItem item) : ObservableObject
{
    public EarItem Item { get; } = item;

    [ObservableProperty]
    private string _label = "";

    [ObservableProperty]
    private string _toolTip = "";

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => owner.OnChoiceToggled(this, value);
}

/// <summary>One chord type's checkboxes ("Sus4": the degrees where the key has one).</summary>
public sealed record EarChordGroup(string Title, IReadOnlyList<EarChoiceViewModel> Choices);

/// <summary>An answer button. Before answering it answers; afterwards it plays that answer for comparison.</summary>
public sealed partial class EarAnswerViewModel(EarTrainerViewModel owner, EarItem item, string label, string detail, int number) : ObservableObject
{
    public EarItem Item { get; } = item;
    public string Label { get; } = label;
    /// <summary>Second line: "minor 3rd", "Dm (minor)".</summary>
    public string Detail { get; } = detail;

    public string ToolTip => $"{Label} · {Detail}";

    /// <summary>Keyboard shortcut 1–9, 0 (= the 10th); −1 = none.</summary>
    public int Number { get; } = number;

    public string NumberText => Number < 0 ? "" : Number.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private bool _isCorrect;

    [ObservableProperty]
    private bool _isWrong;

    [RelayCommand]
    private void Choose() => owner.Choose(this);
}

/// <summary>A scale-degree button for melodic dictation: "3" over "E" (key 3).</summary>
public sealed partial class DegreeKeyViewModel(EarTrainerViewModel owner, int degree, string note)
{
    public int Degree { get; } = degree;
    public string Label { get; } = MelodyDictation.DegreeLabel(degree);
    public string Note { get; } = note;

    [RelayCommand]
    private void Enter() => owner.EnterDegree(Degree);
}

/// <summary>One note of the melody being written down.</summary>
public sealed partial class MelodySlotViewModel : ObservableObject
{
    [ObservableProperty]
    private string _text = "·";

    /// <summary>After scoring, under a wrong note: what it was.</summary>
    [ObservableProperty]
    private string _answer = "";

    [ObservableProperty]
    private bool _isNext;

    [ObservableProperty]
    private bool _isCorrect;

    [ObservableProperty]
    private bool _isWrong;
}

/// <summary>Per-item score line: this round and all time.</summary>
public sealed record EarScoreRow(string Label, string Session, string AllTime, double Accuracy, bool HasAllTime);

/// <summary>
/// Tools → Ear Trainer: intervals or chords (triads, 7ths, sus, add9, 6ths, 9ths built from the key's notes) in a chosen
/// key, or melodic dictation; hear it, answer, get scored. Questions and scoring are in <see cref="EarQuiz"/> and
/// <see cref="MelodyDictation"/> (Domain); notes are rendered by <see cref="PluckSynth"/> and played by a
/// <see cref="ClipPlayer"/> on this window's own output stream. "Log to today" appends the round's score to today's
/// progress notes of an Ear Training block.
/// </summary>
public sealed partial class EarTrainerViewModel : ObservableObject, IDisposable
{
    public const string SettingsKey = "earTrainer";
    public const string StatsKey = "earTrainerStats";

    /// <summary>The built-in Ear Training category (IDs are category × 100 + n).</summary>
    public const int EarTrainingCategoryId = 9;

    private static readonly TimeSpan AutoNextDelay = TimeSpan.FromSeconds(1.2);
    private static readonly int[] DefaultIntervals = [3, 4, 5, 7];
    private static readonly string[] DefaultChords = ["0", "3", "4", "5"];

    private readonly IAudioOutput _output;
    private readonly SettingsStore _settingsStore;
    private readonly LibraryStore _library;
    private readonly ProgressStore _progress;
    private readonly TimeProvider _clock;
    private readonly ClipPlayer _player = new();
    private readonly DispatcherTimer _autoNextTimer;
    private readonly Dictionary<string, EarStat> _allTime;
    private readonly HashSet<string> _chordIds;
    private readonly bool _loading;
    private bool _building;
    private EarQuiz? _quiz;
    private MelodyDictation? _melody;
    private int _sampleRate;
    private int _loggedAtAsked;

    public EarTrainerViewModel(IAudioDevices devices, SettingsStore settingsStore, LibraryStore library, ProgressStore progress, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(clock);
        _output = devices.CreateOutput(); // owned: disposed with the window
        _settingsStore = settingsStore;
        _library = library;
        _progress = progress;
        _clock = clock;
        _autoNextTimer = new DispatcherTimer(AutoNextDelay, DispatcherPriority.Normal, (_, _) => AutoAdvance());
        _autoNextTimer.Stop();
        _allTime = Load<EarTrainerStats>(StatsKey)?.Items ?? [];

        _loading = true;
        var s = Load<EarTrainerSettings>(SettingsKey) ?? new EarTrainerSettings();
        _chordIds = [.. s.Chords ?? [.. DefaultChords]];
        foreach (var item in EarItem.AllIntervals) IntervalChoices.Add(new EarChoiceViewModel(this, item));
        foreach (var c in IntervalChoices) c.IsSelected = (s.Intervals ?? [.. DefaultIntervals]).Contains(c.Item.Value);
        SelectedMode = ModeOptions.FirstOrDefault(o => o.Value == s.Mode) ?? ModeOptions[0];
        SelectedTonic = TonicOptions.FirstOrDefault(o => o.Value == ((s.Tonic % 12) + 12) % 12) ?? TonicOptions[0];
        SelectedKeyMode = KeyModeOptions.FirstOrDefault(o => o.Value == s.KeyMode) ?? KeyModeOptions[0];
        SelectedDirection = DirectionOptions.FirstOrDefault(o => o.Value == s.Direction) ?? DirectionOptions[0];
        SelectedRoot = RootOptions.FirstOrDefault(o => o.Value == s.Root) ?? RootOptions[0];
        SelectedMelodyLength = Math.Clamp(s.MelodyLength, MelodyDictation.MinLength, MelodyDictation.MaxLength);
        PlayKeyFirst = s.PlayKeyFirst;
        AutoNext = s.AutoNext;
        Volume = Math.Clamp(s.Volume, 0, 100);
        _player.Volume = (float)(Volume / 100.0);
        _loading = false;

        BuildChordGroups();
        BuildDegreeKeys();
        RefreshLabels();
        LoadLogBlocks();
        ResetRound();
    }

    // ---- Options ----

    public IReadOnlyList<LabeledOption<EarItemKind>> ModeOptions { get; } =
        [new(EarItemKind.Interval, "Intervals"), new(EarItemKind.Chord, "Chords"), new(EarItemKind.Melody, "Melodies")];

    public IReadOnlyList<LabeledOption<int>> TonicOptions { get; } =
        [.. Enumerable.Range(0, 12).Select(pc => new LabeledOption<int>(pc, new MusicKey(pc, KeyMode.Major).NoteName(pc) is var sharp
            && new MusicKey(pc, KeyMode.Minor).NoteName(pc) is var other && sharp != other ? $"{sharp} / {other}" : sharp))];

    public IReadOnlyList<LabeledOption<KeyMode>> KeyModeOptions { get; } =
        [new(KeyMode.Major, "major"), new(KeyMode.Minor, "minor (natural)")];

    public IReadOnlyList<LabeledOption<IntervalDirection>> DirectionOptions { get; } =
    [
        new(IntervalDirection.Ascending, "Ascending (low, then high)"),
        new(IntervalDirection.Descending, "Descending (high, then low)"),
        new(IntervalDirection.Harmonic, "Harmonic (together)"),
        new(IntervalDirection.Mixed, "Mixed"),
    ];

    public IReadOnlyList<LabeledOption<IntervalRoot>> RootOptions { get; } =
    [
        new(IntervalRoot.Tonic, "From the key's root"),
        new(IntervalRoot.AnyScaleNote, "From any note of the key"),
    ];

    public IReadOnlyList<int> MelodyLengthOptions { get; } =
        [.. Enumerable.Range(MelodyDictation.MinLength, MelodyDictation.MaxLength - MelodyDictation.MinLength + 1)];

    public ObservableCollection<EarChoiceViewModel> IntervalChoices { get; } = [];
    public ObservableCollection<EarChordGroup> ChordGroups { get; } = [];
    public ObservableCollection<EarAnswerViewModel> Answers { get; } = [];
    public ObservableCollection<DegreeKeyViewModel> DegreeKeys { get; } = [];
    public ObservableCollection<MelodySlotViewModel> MelodySlots { get; } = [];
    public ObservableCollection<EarScoreRow> ScoreRows { get; } = [];

    // ---- Settings ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIntervals), nameof(IsChords), nameof(IsMelodies), nameof(IsItemQuiz), nameof(ShowPlayKeyFirst))]
    private LabeledOption<EarItemKind>? _selectedMode;

    [ObservableProperty]
    private LabeledOption<int>? _selectedTonic;

    [ObservableProperty]
    private LabeledOption<KeyMode>? _selectedKeyMode;

    [ObservableProperty]
    private LabeledOption<IntervalDirection>? _selectedDirection;

    [ObservableProperty]
    private LabeledOption<IntervalRoot>? _selectedRoot;

    [ObservableProperty]
    private int _selectedMelodyLength = 4;

    [ObservableProperty]
    private bool _playKeyFirst = true;

    [ObservableProperty]
    private bool _autoNext = true;

    [ObservableProperty]
    private double _volume = 80;

    private EarItemKind Mode => SelectedMode?.Value ?? EarItemKind.Interval;
    public bool IsIntervals => Mode == EarItemKind.Interval;
    public bool IsChords => Mode == EarItemKind.Chord;
    public bool IsMelodies => Mode == EarItemKind.Melody;

    /// <summary>Intervals or chords: answered with one button per item.</summary>
    public bool IsItemQuiz => !IsMelodies;

    /// <summary>Chord questions are just the chord (user's choice); intervals and melodies can start with the key chord.</summary>
    public bool ShowPlayKeyFirst => !IsChords;

    private MusicKey Key => new(SelectedTonic?.Value ?? 0, SelectedKeyMode?.Value ?? KeyMode.Major);

    // ---- Round state ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReplayCommand), nameof(PlayKeyCommand))]
    private bool _hasQuestion;

    [ObservableProperty]
    private string _prompt = "";

    [ObservableProperty]
    private string _feedback = "";

    [ObservableProperty]
    private bool _feedbackIsGood;

    [ObservableProperty]
    private string _scoreText = "";

    [ObservableProperty]
    private string _nextText = "▶ Start";

    [ObservableProperty]
    private string? _error;

    partial void OnSelectedModeChanged(LabeledOption<EarItemKind>? value)
    {
        SettingsChanged();
        if (!_loading) SelectDefaultLogBlock();
    }

    partial void OnSelectedTonicChanged(LabeledOption<int>? value) => SettingsChanged(relabel: true);
    partial void OnSelectedKeyModeChanged(LabeledOption<KeyMode>? value) => SettingsChanged(relabel: true);
    partial void OnSelectedDirectionChanged(LabeledOption<IntervalDirection>? value) => SettingsChanged();
    partial void OnSelectedRootChanged(LabeledOption<IntervalRoot>? value) => SettingsChanged();
    partial void OnSelectedMelodyLengthChanged(int value) => SettingsChanged();
    partial void OnPlayKeyFirstChanged(bool value) => SettingsChanged();
    partial void OnAutoNextChanged(bool value) => SaveSettings();
    partial void OnVolumeChanged(double value)
    {
        _player.Volume = (float)(value / 100.0);
        if (!_loading) SaveSettings();
    }

    internal void OnChoiceToggled(EarChoiceViewModel choice, bool selected)
    {
        if (choice.Item.Kind == EarItemKind.Chord)
        {
            // Chord picks are kept by id, so a type missing in this mode (e.g. after major → minor) stays picked for later.
            if (selected) _chordIds.Add(ChordId(choice.Item));
            else _chordIds.Remove(ChordId(choice.Item));
        }
        if (!_building) SettingsChanged();
    }

    /// <summary>What's asked changed: the round starts over (all-time scores are kept).</summary>
    private void SettingsChanged(bool relabel = false)
    {
        if (_loading) return;
        if (relabel)
        {
            BuildChordGroups();
            BuildDegreeKeys();
            RefreshLabels();
        }
        SaveSettings();
        ResetRound();
    }

    private List<EarItem> SelectedItems => Mode switch
    {
        EarItemKind.Interval => [.. IntervalChoices.Where(c => c.IsSelected).Select(c => c.Item)],
        EarItemKind.Chord => [.. ChordGroups.SelectMany(g => g.Choices).Where(c => c.IsSelected).Select(c => c.Item)],
        _ => [],
    };

    private static string GroupTitle(ChordType type) => type switch
    {
        ChordType.Triad => "Triads",
        ChordType.Seventh => "Seventh chords",
        ChordType.Sus2 => "Sus2",
        ChordType.Sus4 => "Sus4",
        ChordType.Add9 => "Add9",
        ChordType.Sixth => "6th chords",
        _ => "9th chords",
    };

    /// <summary>One group per chord type, holding only the degrees where the key has that chord.</summary>
    private void BuildChordGroups()
    {
        _building = true;
        ChordGroups.Clear();
        var key = Key;
        foreach (var type in Enum.GetValues<ChordType>())
        {
            var choices = EarItem.AllChords.Where(i => i.Chord == type && i.ExistsIn(key))
                .Select(i => new EarChoiceViewModel(this, i) { IsSelected = _chordIds.Contains(ChordId(i)) })
                .ToList();
            if (choices.Count > 0) ChordGroups.Add(new EarChordGroup(GroupTitle(type), choices));
        }
        _building = false;
    }

    private void BuildDegreeKeys()
    {
        DegreeKeys.Clear();
        var key = Key;
        for (var d = 0; d < MelodyDictation.Degrees; d++)
            DegreeKeys.Add(new DegreeKeyViewModel(this, d, key.NoteName(key.PitchClass + key.DegreeOffset(d))));
    }

    private void RefreshLabels()
    {
        var key = Key;
        foreach (var c in IntervalChoices.Concat(ChordGroups.SelectMany(g => g.Choices)))
        {
            c.Label = c.Item.Kind == EarItemKind.Chord ? c.Item.Label(key) : c.Item.ShortLabel(key);
            c.ToolTip = c.Item.Label(key);
        }
    }

    private void ResetRound()
    {
        _autoNextTimer.Stop();
        _player.Stop();
        _quiz = null;
        _melody = null;
        _loggedAtAsked = 0;
        HasQuestion = false;
        Answers.Clear();
        MelodySlots.Clear();
        Feedback = "";
        LogStatus = null;
        NextText = "▶ Start";
        var items = SelectedItems;
        Prompt = IsMelodies
            ? $"Melodies of {SelectedMelodyLength} notes in {Key.Name}. Press Start (Enter)."
            : items.Count < 2
                ? "Tick at least two to tell apart."
                : $"{items.Count} {(IsIntervals ? "intervals" : "chords")} in {Key.Name}. Press Start (Enter).";
        RefreshScore();
        NextCommand.NotifyCanExecuteChanged();
        BackspaceCommand.NotifyCanExecuteChanged();
        PlayMineCommand.NotifyCanExecuteChanged();
        LogCommand.NotifyCanExecuteChanged();
    }

    // ---- Commands ----

    private bool CanNext() => IsMelodies || SelectedItems.Count >= 2;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next()
    {
        _autoNextTimer.Stop();
        if (IsMelodies)
        {
            NextMelody();
            return;
        }
        var items = SelectedItems;
        if (items.Count < 2) return;
        if (_quiz is null)
        {
            _quiz = new EarQuiz(new EarQuizOptions(Key, items, SelectedDirection?.Value ?? IntervalDirection.Ascending,
                SelectedRoot?.Value ?? IntervalRoot.Tonic, PlayKeyFirst), new Random());
            Answers.Clear();
            var n = 0;
            foreach (var item in _quiz.Options.Items)
            {
                n++;
                Answers.Add(new EarAnswerViewModel(this, item, item.ShortLabel(Key), item.Detail(Key), n <= 10 ? n % 10 : -1));
            }
        }

        var question = _quiz.Next();
        foreach (var a in Answers) a.IsCorrect = a.IsWrong = false;
        HasQuestion = true;
        Feedback = "";
        NextText = "Next ▶";
        Prompt = IsIntervals
            ? $"Which interval? ({DirectionWord(question.Direction)})"
            : $"Which chord in {Key.Name}?  (K: hear the key)";
        Play(question.Sound);
    }

    private static string DirectionWord(IntervalDirection d) => d switch
    {
        IntervalDirection.Descending => "descending",
        IntervalDirection.Harmonic => "together",
        _ => "ascending",
    };

    [RelayCommand(CanExecute = nameof(HasQuestion))]
    private void Replay()
    {
        if (IsMelodies && _melody?.Current is { } m) Play(_melody.SoundOf(m.Degrees));
        else if (_quiz?.Current is { } q) Play(q.Sound);
    }

    [RelayCommand(CanExecute = nameof(HasQuestion))]
    private void PlayKey()
    {
        if (IsMelodies && _melody is not null) Play(_melody.SoundOf([], withKey: true));
        else if (_quiz is not null) Play(_quiz.KeySound());
    }

    /// <summary>Reset asks first: the button turns into "Forget all results? Reset / Cancel".</summary>
    [ObservableProperty]
    private bool _isConfirmingReset;

    [RelayCommand]
    private void CancelReset() => IsConfirmingReset = false;

    [RelayCommand]
    private void ResetAllTime()
    {
        if (!IsConfirmingReset)
        {
            IsConfirmingReset = true;
            return;
        }
        IsConfirmingReset = false;
        _allTime.Clear();
        Save(StatsKey, new EarTrainerStats(_allTime));
        RefreshScore();
    }

    /// <summary>Keys 1–9, 0: an answer button; in melodies 1–8 write a degree down.</summary>
    public void ChooseNumber(int number)
    {
        if (IsMelodies)
        {
            if (number is >= 1 and <= MelodyDictation.Degrees) EnterDegree(number - 1);
            return;
        }
        if (Answers.FirstOrDefault(a => a.Number == number) is { } answer) Choose(answer);
    }

    internal void Choose(EarAnswerViewModel answer)
    {
        if (_quiz?.Current is null) return;
        var result = _quiz.Answer(answer.Item);
        if (!result.Counted)
        {
            Play(_quiz.SoundOf(answer.Item)); // after answering: hear any option to compare
            return;
        }

        var key = Key;
        var statsKey = result.Actual.StatsKey(key);
        _allTime[statsKey] = _allTime.GetValueOrDefault(statsKey).Add(result.IsCorrect);
        Save(StatsKey, new EarTrainerStats(_allTime));

        foreach (var a in Answers)
        {
            a.IsCorrect = a.Item == result.Actual;
            a.IsWrong = a.Item == answer.Item && !result.IsCorrect;
        }
        FeedbackIsGood = result.IsCorrect;
        Feedback = result.IsCorrect
            ? $"✓ {result.Actual.Describe(key)}"
            : $"✗ It was {result.Actual.Describe(key)}. You picked {answer.Item.ShortLabel(key)}: click any answer to compare.";
        RefreshScore();
        LogCommand.NotifyCanExecuteChanged();
        if (result.IsCorrect && AutoNext) _autoNextTimer.Start();
    }

    private void AutoAdvance()
    {
        _autoNextTimer.Stop();
        if (_quiz?.IsAnswered == true || _melody?.Result is not null) Next();
    }

    // ---- Melodic dictation ----

    private string MelodyStatsKey => $"mel:{SelectedMelodyLength}";

    private void NextMelody()
    {
        _melody ??= new MelodyDictation(new MelodyOptions(Key, SelectedMelodyLength, PlayKeyFirst), new Random());
        var question = _melody.Next();
        MelodySlots.Clear();
        foreach (var _ in question.Degrees) MelodySlots.Add(new MelodySlotViewModel());
        RefreshSlots();
        HasQuestion = true;
        Feedback = "";
        NextText = "Next ▶";
        Prompt = $"Write down the melody ({question.Degrees.Count} notes) with the degrees: 1 = {Key.NoteName(Key.PitchClass)}";
        Play(question.Sound);
        BackspaceCommand.NotifyCanExecuteChanged();
        PlayMineCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Degree buttons and keys 1–8.</summary>
    public void EnterDegree(int degree)
    {
        if (_melody?.Current is null || _melody.Result is not null) return;
        Play(_melody.SoundOf([degree]));                     // hear the note you wrote
        var result = _melody.Enter(degree);
        RefreshSlots();
        BackspaceCommand.NotifyCanExecuteChanged();
        PlayMineCommand.NotifyCanExecuteChanged();
        if (result is null) return;

        _allTime[MelodyStatsKey] = _allTime.GetValueOrDefault(MelodyStatsKey).Add(result.AllCorrect);
        Save(StatsKey, new EarTrainerStats(_allTime));
        var melody = string.Join(" ", _melody.Current.Degrees.Select(MelodyDictation.DegreeLabel));
        var mine = string.Join(" ", result.Entered.Select(MelodyDictation.DegreeLabel));
        FeedbackIsGood = result.AllCorrect;
        Feedback = result.AllCorrect
            ? $"✓ All {result.NoteCorrect.Count} notes: {melody}"
            : $"✗ {result.CorrectNotes} of {result.NoteCorrect.Count} notes. It was {melody} (yours: {mine}). ↻ Replay and ▶ Mine to compare.";
        RefreshScore();
        LogCommand.NotifyCanExecuteChanged();
        if (result.AllCorrect && AutoNext) _autoNextTimer.Start();
    }

    private bool CanBackspace() => _melody is { Result: null, Entered.Count: > 0 };

    [RelayCommand(CanExecute = nameof(CanBackspace))]
    private void Backspace()
    {
        _melody?.Backspace();
        RefreshSlots();
        BackspaceCommand.NotifyCanExecuteChanged();
        PlayMineCommand.NotifyCanExecuteChanged();
    }

    private bool CanPlayMine() => _melody is { Entered.Count: > 0 };

    /// <summary>Hear what you wrote down.</summary>
    [RelayCommand(CanExecute = nameof(CanPlayMine))]
    private void PlayMine()
    {
        if (_melody is not null) Play(_melody.SoundOf(_melody.Entered));
    }

    private void RefreshSlots()
    {
        if (_melody?.Current is not { } question) return;
        var entered = _melody.Entered;
        var result = _melody.Result;
        for (var i = 0; i < MelodySlots.Count; i++)
        {
            var slot = MelodySlots[i];
            slot.Text = i < entered.Count ? MelodyDictation.DegreeLabel(entered[i]) : "·";
            slot.IsNext = result is null && i == entered.Count;
            slot.IsCorrect = result is not null && result.NoteCorrect[i];
            slot.IsWrong = result is not null && !result.NoteCorrect[i];
            slot.Answer = slot.IsWrong ? MelodyDictation.DegreeLabel(question.Degrees[i]) : "";
        }
    }

    // ---- Score ----

    private void RefreshScore()
    {
        var key = Key;
        ScoreRows.Clear();
        if (IsMelodies)
        {
            ScoreText = _melody is { Melodies.Asked: > 0 } m
                ? $"Melodies {m.Melodies.Correct}/{m.Melodies.Asked} right · notes {m.Notes.Correct}/{m.Notes.Asked} · {m.Notes.Accuracy:P0}"
                : "No melodies yet this round.";
            var session = _melody?.Melodies ?? default;
            var all = _allTime.GetValueOrDefault(MelodyStatsKey);
            ScoreRows.Add(new EarScoreRow($"{SelectedMelodyLength} notes",
                session.Asked > 0 ? $"{session.Correct}/{session.Asked}" : "–",
                all.Asked > 0 ? $"{all.Accuracy:P0} of {all.Asked}" : "not tried",
                all.Accuracy ?? 0, all.Asked > 0));
            return;
        }

        ScoreText = _quiz is { Score.Asked: > 0 } quiz
            ? $"Score {quiz.Score.Correct}/{quiz.Score.Asked} · {quiz.Score.Accuracy:P0} · streak {quiz.Streak} (best {quiz.BestStreak})"
            : "No answers yet this round.";
        foreach (var item in SelectedItems.Where(i => i.ExistsIn(key)))
        {
            var session = _quiz?.ByItem.GetValueOrDefault(item) ?? default;
            var all = _allTime.GetValueOrDefault(item.StatsKey(key));
            ScoreRows.Add(new EarScoreRow(
                item.ShortLabel(key),
                session.Asked > 0 ? $"{session.Correct}/{session.Asked}" : "–",
                all.Asked > 0 ? $"{all.Accuracy:P0} of {all.Asked}" : "not tried",
                all.Accuracy ?? 0,
                all.Asked > 0));
        }
    }

    // ---- Log to today's Ear Training block ----

    /// <summary>The Ear Training category's items in the library (its id is fixed); empty if it was removed.</summary>
    public ObservableCollection<LabeledOption<int>> LogBlocks { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LogCommand))]
    private LabeledOption<int>? _selectedLogBlock;

    [ObservableProperty]
    private string? _logStatus;

    public bool HasLogBlocks => LogBlocks.Count > 0;

    private void LoadLogBlocks()
    {
        LogBlocks.Clear();
        try
        {
            if (_library.LoadLibrary().FirstOrDefault(c => c.Id == EarTrainingCategoryId) is { } category)
                foreach (var s in category.Subcategories) LogBlocks.Add(new LabeledOption<int>(s.Id, s.DisplayName));
        }
#pragma warning disable CA1031 // Logging is optional: the window works without it.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Loading the Ear Training blocks failed: {ex.Message}");
        }
#pragma warning restore CA1031
        OnPropertyChanged(nameof(HasLogBlocks));
        SelectDefaultLogBlock();
    }

    /// <summary>Intervals → Interval recognition (901), chords → Chord quality ID (902), melodies → Call &amp; response (905).</summary>
    private void SelectDefaultLogBlock()
    {
        var preferred = Mode switch
        {
            EarItemKind.Chord => EarTrainingCategoryId * 100 + 2,
            EarItemKind.Melody => EarTrainingCategoryId * 100 + 5,
            _ => EarTrainingCategoryId * 100 + 1,
        };
        SelectedLogBlock = LogBlocks.FirstOrDefault(b => b.Value == preferred) ?? LogBlocks.FirstOrDefault();
    }

    private int AskedThisRound => IsMelodies ? _melody?.Melodies.Asked ?? 0 : _quiz?.Score.Asked ?? 0;

    private bool CanLog() => SelectedLogBlock is not null && AskedThisRound > _loggedAtAsked;

    /// <summary>"intervals in C major: 18/20 (90 %), best streak 7"; "4-note melodies in C major: 7/10 melodies, 34/40 notes (85 %)".</summary>
    public string? RoundSummary()
    {
        var key = Key.Name;
        if (IsMelodies)
            return _melody is { Melodies.Asked: > 0 } m
                ? $"{SelectedMelodyLength}-note melodies in {key}: {m.Melodies.Correct}/{m.Melodies.Asked} melodies, " +
                  $"{m.Notes.Correct}/{m.Notes.Asked} notes ({m.Notes.Accuracy:P0})"
                : null;
        return _quiz is { Score.Asked: > 0 } q
            ? $"{(IsIntervals ? "intervals" : "chords")} in {key}: {q.Score.Correct}/{q.Score.Asked} ({q.Score.Accuracy:P0}), best streak {q.BestStreak}"
            : null;
    }

    /// <summary>Appends the round's score to today's notes of the chosen Ear Training block (keeps its exercises and rating).</summary>
    [RelayCommand(CanExecute = nameof(CanLog))]
    private void Log()
    {
        if (SelectedLogBlock is not { } block || RoundSummary() is not { } summary) return;
        var now = _clock.GetLocalNow();
        var today = DateOnly.FromDateTime(now.DateTime);
        var line = $"Ear trainer {now:HH:mm}: {summary}";
        try
        {
            var entry = _progress.Get(today, block.Value);
            var notes = string.IsNullOrWhiteSpace(entry?.Notes) ? line : entry.Notes.TrimEnd() + Environment.NewLine + line;
            _progress.Save(today, block.Value,
                entry?.Exercises.Select(x => new ExerciseLog(x.Name, x.Bpm, x.NoteValue, x.RemovedRun)) ?? [],
                entry?.Rating, notes);
            _loggedAtAsked = AskedThisRound;
            LogStatus = $"Added to today's “{block.Label}” notes: {line}";
        }
#pragma warning disable CA1031 // Shown, not thrown.
        catch (Exception ex)
        {
            LogStatus = $"Couldn't log it: {ex.Message}";
        }
#pragma warning restore CA1031
        LogCommand.NotifyCanExecuteChanged();
    }

    // ---- Audio ----

    private void Play(EarSound sound)
    {
        Error = null;
        try
        {
            if (!_output.IsRunning)
            {
                _sampleRate = _output.GetPreferredSampleRate();
                _output.Start(new AudioStreamOptions(SampleRate: _sampleRate, FramesPerBuffer: 256), _player.Render);
            }
            _player.Play(PluckSynth.Render(_sampleRate, sound.Notes.Select(n => new SynthNote(n.Start, n.Midi, n.Duration))));
        }
#pragma warning disable CA1031 // Any audio failure (no device, driver error, missing native lib) is shown, not thrown.
        catch (Exception ex)
        {
            Error = $"Couldn't play audio: {ex.Message}";
        }
#pragma warning restore CA1031
    }

    // ---- Persistence ----

    /// <summary>"4" (V), "47" (V7), "4sus4", "0add9", "16", "49": the triad/7th forms are the ones saved before 2026-10-18.</summary>
    private static string ChordId(EarItem item) => item.Value.ToString(CultureInfo.InvariantCulture) + item.Chord switch
    {
        ChordType.Triad => "",
        ChordType.Seventh => "7",
        ChordType.Sus2 => "sus2",
        ChordType.Sus4 => "sus4",
        ChordType.Add9 => "add9",
        ChordType.Sixth => "6",
        _ => "9",
    };

    private void SaveSettings()
    {
        if (_loading) return;
        Save(SettingsKey, new EarTrainerSettings(
            Mode,
            SelectedTonic?.Value ?? 0,
            SelectedKeyMode?.Value ?? KeyMode.Major,
            [.. IntervalChoices.Where(c => c.IsSelected).Select(c => c.Item.Value)],
            [.. _chordIds.Order(StringComparer.Ordinal)],
            SelectedDirection?.Value ?? IntervalDirection.Ascending,
            SelectedRoot?.Value ?? IntervalRoot.Tonic,
            PlayKeyFirst,
            AutoNext,
            Volume,
            SelectedMelodyLength));
    }

    private T? Load<T>(string key) where T : class
    {
        try
        {
            return _settingsStore.Get<T>(key);
        }
#pragma warning disable CA1031 // A broken preference must not break the window.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Loading {key} failed: {ex.Message}");
            return null;
        }
#pragma warning restore CA1031
    }

    private void Save<T>(string key, T value) where T : class
    {
        try
        {
            _settingsStore.Set(key, value);
        }
#pragma warning disable CA1031 // Losing a preference or a score must not break practice.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Saving {key} failed: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    private bool _disposed;

    public void Dispose()
    {
        // Disposed by the window on close, then again by the DI container when the app exits (after the database
        // services are gone): only the first call may do anything.
        if (_disposed) return;
        _disposed = true;
        _autoNextTimer.Stop();
        _player.Stop();
        _output.Dispose();
    }
}
