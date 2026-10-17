using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GuitarPractice.Audio;
using GuitarPractice.Domain.EarTraining;
using GuitarPractice.Infrastructure;

namespace GuitarPractice.App.ViewModels;

/// <summary>Remembered between sessions (AppSettings key "earTrainer").</summary>
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
    double Volume = 80);

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

    partial void OnIsSelectedChanged(bool value) => owner.OnChoicesChanged();
}

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

    public string NumberText => Number < 0 ? "" : Number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [ObservableProperty]
    private bool _isCorrect;

    [ObservableProperty]
    private bool _isWrong;

    [RelayCommand]
    private void Choose() => owner.Choose(this);
}

/// <summary>Per-item score line: this round and all time.</summary>
public sealed record EarScoreRow(string Label, string Session, string AllTime, double Accuracy, bool HasAllTime);

/// <summary>
/// Tools → Ear Trainer: intervals or diatonic chords in a chosen key; hear one, pick what it was, get scored. Questions
/// and scoring are in <see cref="EarQuiz"/> (Domain); notes are rendered by <see cref="PluckSynth"/> and played by a
/// <see cref="ClipPlayer"/> on this window's own output stream.
/// </summary>
public sealed partial class EarTrainerViewModel : ObservableObject, IDisposable
{
    public const string SettingsKey = "earTrainer";
    public const string StatsKey = "earTrainerStats";

    private static readonly TimeSpan AutoNextDelay = TimeSpan.FromSeconds(1.2);
    private static readonly int[] DefaultIntervals = [3, 4, 5, 7];
    private static readonly string[] DefaultChords = ["0", "3", "4", "5"];

    private readonly IAudioOutput _output;
    private readonly SettingsStore _settingsStore;
    private readonly ClipPlayer _player = new();
    private readonly DispatcherTimer _autoNextTimer;
    private readonly Dictionary<string, EarStat> _allTime;
    private readonly bool _loading;
    private EarQuiz? _quiz;
    private int _sampleRate;

    public EarTrainerViewModel(IAudioDevices devices, SettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(settingsStore);
        _output = devices.CreateOutput(); // owned: disposed with the window
        _settingsStore = settingsStore;
        _autoNextTimer = new DispatcherTimer(AutoNextDelay, DispatcherPriority.Normal, (_, _) => AutoAdvance());
        _autoNextTimer.Stop();
        _allTime = Load<EarTrainerStats>(StatsKey)?.Items ?? [];

        _loading = true;
        var s = Load<EarTrainerSettings>(SettingsKey) ?? new EarTrainerSettings();
        foreach (var item in EarItem.AllIntervals) IntervalChoices.Add(new EarChoiceViewModel(this, item));
        foreach (var item in EarItem.AllChords) (item.Seventh ? SeventhChoices : TriadChoices).Add(new EarChoiceViewModel(this, item));
        foreach (var c in IntervalChoices) c.IsSelected = (s.Intervals ?? [.. DefaultIntervals]).Contains(c.Item.Value);
        foreach (var c in TriadChoices.Concat(SeventhChoices)) c.IsSelected = (s.Chords ?? [.. DefaultChords]).Contains(ChordId(c.Item));
        SelectedMode = ModeOptions.FirstOrDefault(o => o.Value == s.Mode) ?? ModeOptions[0];
        SelectedTonic = TonicOptions.FirstOrDefault(o => o.Value == ((s.Tonic % 12) + 12) % 12) ?? TonicOptions[0];
        SelectedKeyMode = KeyModeOptions.FirstOrDefault(o => o.Value == s.KeyMode) ?? KeyModeOptions[0];
        SelectedDirection = DirectionOptions.FirstOrDefault(o => o.Value == s.Direction) ?? DirectionOptions[0];
        SelectedRoot = RootOptions.FirstOrDefault(o => o.Value == s.Root) ?? RootOptions[0];
        PlayKeyFirst = s.PlayKeyFirst;
        AutoNext = s.AutoNext;
        Volume = Math.Clamp(s.Volume, 0, 100);
        _player.Volume = (float)(Volume / 100.0);
        _loading = false;

        RefreshLabels();
        ResetRound();
    }

    // ---- Options ----

    public IReadOnlyList<LabeledOption<EarItemKind>> ModeOptions { get; } =
        [new(EarItemKind.Interval, "Intervals"), new(EarItemKind.Chord, "Chords")];

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

    public ObservableCollection<EarChoiceViewModel> IntervalChoices { get; } = [];
    public ObservableCollection<EarChoiceViewModel> TriadChoices { get; } = [];
    public ObservableCollection<EarChoiceViewModel> SeventhChoices { get; } = [];
    public ObservableCollection<EarAnswerViewModel> Answers { get; } = [];
    public ObservableCollection<EarScoreRow> ScoreRows { get; } = [];

    // ---- Settings ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIntervals), nameof(IsChords))]
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
    private bool _playKeyFirst = true;

    [ObservableProperty]
    private bool _autoNext = true;

    [ObservableProperty]
    private double _volume = 80;

    public bool IsIntervals => SelectedMode?.Value != EarItemKind.Chord;
    public bool IsChords => !IsIntervals;

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

    partial void OnSelectedModeChanged(LabeledOption<EarItemKind>? value) => SettingsChanged();
    partial void OnSelectedTonicChanged(LabeledOption<int>? value) => SettingsChanged(relabel: true);
    partial void OnSelectedKeyModeChanged(LabeledOption<KeyMode>? value) => SettingsChanged(relabel: true);
    partial void OnSelectedDirectionChanged(LabeledOption<IntervalDirection>? value) => SettingsChanged();
    partial void OnSelectedRootChanged(LabeledOption<IntervalRoot>? value) => SettingsChanged();
    partial void OnPlayKeyFirstChanged(bool value) => SettingsChanged();
    partial void OnAutoNextChanged(bool value) => SaveSettings();
    partial void OnVolumeChanged(double value)
    {
        _player.Volume = (float)(value / 100.0);
        if (!_loading) SaveSettings();
    }

    internal void OnChoicesChanged() => SettingsChanged();

    /// <summary>What's asked changed: the round starts over (all-time scores are kept).</summary>
    private void SettingsChanged(bool relabel = false)
    {
        if (_loading) return;
        if (relabel) RefreshLabels();
        SaveSettings();
        ResetRound();
    }

    private List<EarItem> SelectedItems => IsIntervals
        ? [.. IntervalChoices.Where(c => c.IsSelected).Select(c => c.Item)]
        : [.. TriadChoices.Concat(SeventhChoices).Where(c => c.IsSelected).Select(c => c.Item)];

    private void RefreshLabels()
    {
        var key = Key;
        foreach (var c in IntervalChoices.Concat(TriadChoices).Concat(SeventhChoices))
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
        HasQuestion = false;
        Answers.Clear();
        Feedback = "";
        NextText = "▶ Start";
        var items = SelectedItems;
        Prompt = items.Count < 2
            ? "Tick at least two to tell apart."
            : $"{items.Count} {(IsIntervals ? "intervals" : "chords")} in {Key.Name}. Press Start (Enter).";
        RefreshScore();
        NextCommand.NotifyCanExecuteChanged();
    }

    // ---- Commands ----

    private bool CanNext() => SelectedItems.Count >= 2;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next()
    {
        _autoNextTimer.Stop();
        var items = SelectedItems;
        if (items.Count < 2) return;
        if (_quiz is null)
        {
            _quiz = new EarQuiz(new EarQuizOptions(Key, items, SelectedDirection?.Value ?? IntervalDirection.Ascending,
                SelectedRoot?.Value ?? IntervalRoot.Tonic, PlayKeyFirst), new Random());
            Answers.Clear();
            var n = 0;
            foreach (var item in items)
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
        if (_quiz?.Current is { } q) Play(q.Sound);
    }

    [RelayCommand(CanExecute = nameof(HasQuestion))]
    private void PlayKey()
    {
        if (_quiz is not null) Play(_quiz.KeySound());
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

    /// <summary>Answer buttons and keys 1–9, 0.</summary>
    public void ChooseNumber(int number)
    {
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
        if (result.IsCorrect && AutoNext) _autoNextTimer.Start();
    }

    private void AutoAdvance()
    {
        _autoNextTimer.Stop();
        if (_quiz?.IsAnswered == true) Next();
    }

    private void RefreshScore()
    {
        var key = Key;
        if (_quiz is { Score.Asked: > 0 } quiz)
        {
            ScoreText = $"Score {quiz.Score.Correct}/{quiz.Score.Asked} · {quiz.Score.Accuracy:P0} · streak {quiz.Streak} (best {quiz.BestStreak})";
        }
        else ScoreText = "No answers yet this round.";

        ScoreRows.Clear();
        foreach (var item in SelectedItems)
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

    private static string ChordId(EarItem item) => item.Seventh ? $"{item.Value}7" : $"{item.Value}";

    private void SaveSettings()
    {
        if (_loading) return;
        Save(SettingsKey, new EarTrainerSettings(
            SelectedMode?.Value ?? EarItemKind.Interval,
            SelectedTonic?.Value ?? 0,
            SelectedKeyMode?.Value ?? KeyMode.Major,
            [.. IntervalChoices.Where(c => c.IsSelected).Select(c => c.Item.Value)],
            [.. TriadChoices.Concat(SeventhChoices).Where(c => c.IsSelected).Select(c => ChordId(c.Item))],
            SelectedDirection?.Value ?? IntervalDirection.Ascending,
            SelectedRoot?.Value ?? IntervalRoot.Tonic,
            PlayKeyFirst,
            AutoNext,
            Volume));
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

    public void Dispose()
    {
        _autoNextTimer.Stop();
        _player.Stop();
        _output.Dispose();
    }
}
