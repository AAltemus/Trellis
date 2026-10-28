using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GuitarPractice.Audio;

namespace GuitarPractice.App.ViewModels;

public sealed record SoundOption(MetronomeSound Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A pattern in the picker: built in (can't be overwritten or deleted) or saved by the user.</summary>
public sealed record DrumPatternOption(DrumPattern Pattern, bool IsBuiltIn)
{
    public override string ToString() => IsBuiltIn ? $"{Pattern.Name} (built in)" : Pattern.Name;
}

/// <summary>The user's saved drum patterns (AppSettings key "drumPatterns").</summary>
public sealed record DrumPatternLibrary(List<DrumPattern> Patterns);

/// <summary>
/// Drum sound for the metronome: the click ⇄ drums switch, the step grid (kick / snare / closed hi-hat on each step of
/// the bar; steps = beats per bar × subdivision) and the user's saved patterns. The grid always matches the meter:
/// changing beats or subdivision resizes it (<see cref="DrumPattern.Resize"/>) and picking a pattern sets the meter.
/// </summary>
public sealed partial class MetronomeViewModel
{
    public const string PatternsKey = "drumPatterns";

    private DrumPattern _pattern = DrumPattern.BasicRock;
    private bool _applyingPattern;
    private DrumStepViewModel? _nowStep;

    public IReadOnlyList<SoundOption> SoundOptions { get; } =
    [
        new(MetronomeSound.Click, "Click (beep)"),
        new(MetronomeSound.Drums, "Drums"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDrums), nameof(IsBackbeatOn))]
    private SoundOption? _selectedSound;

    public bool IsDrums => SelectedSound?.Value == MetronomeSound.Drums;

    /// <summary>The grid: one group per beat, one column per step, rows hi-hat / snare / kick.</summary>
    public ObservableCollection<DrumBeatViewModel> DrumBeats { get; } = [];

    public ObservableCollection<DrumPatternOption> Patterns { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeletePattern))]
    [NotifyCanExecuteChangedFor(nameof(DeletePatternCommand))]
    private DrumPatternOption? _selectedPattern;

    [ObservableProperty]
    private string _patternName = "";

    [ObservableProperty]
    private string? _patternStatus;

    /// <summary>Delete asks first: the button turns into "Delete “X”?" + Cancel.</summary>
    [ObservableProperty]
    private bool _isConfirmingDelete;

    public bool CanDeletePattern => SelectedPattern is { IsBuiltIn: false };

    partial void OnSelectedSoundChanged(SoundOption? value)
    {
        RebuildBeatLights(); // the backbeat lights apply to the click only
        Publish();
    }

    partial void OnSelectedPatternChanged(DrumPatternOption? value)
    {
        IsConfirmingDelete = false;
        if (value is null || _loading) return;
        UsePattern(value.Pattern);
        PatternStatus = null;
    }

    /// <summary>Makes <paramref name="pattern"/> the current one; the meter follows it.</summary>
    private void UsePattern(DrumPattern pattern)
    {
        _applyingPattern = true;
        try
        {
            BeatsPerBar = pattern.BeatsPerBar;
            SelectedSubdivision = SubdivisionOptions.First(o => o.Value == pattern.Subdivision);
        }
        finally
        {
            _applyingPattern = false;
        }
        _pattern = pattern;
        PatternName = pattern.Name;
        RebuildDrumGrid();
        Publish();
    }

    /// <summary>Beats per bar or subdivision changed: keep the grid on the meter.</summary>
    private void FitPatternToMeter()
    {
        if (_applyingPattern) return;
        var subdivision = SelectedSubdivision?.Value ?? Subdivision.Quarters;
        if (_pattern.BeatsPerBar == BeatsPerBar && _pattern.Subdivision == subdivision) return;
        _pattern = _pattern.Resize(BeatsPerBar, subdivision);
        RebuildDrumGrid();
    }

    private void RebuildDrumGrid()
    {
        _nowStep = null;
        DrumBeats.Clear();
        var perBeat = _pattern.StepsPerBeat;
        for (var b = 0; b < _pattern.BeatsPerBar; b++)
        {
            var steps = Enumerable.Range(0, perBeat)
                .Select(n => new DrumStepViewModel(this, b * perBeat + n, _pattern.Steps[b * perBeat + n], isOnBeat: n == 0))
                .ToList();
            DrumBeats.Add(new DrumBeatViewModel(b + 1, steps));
        }
    }

    internal void OnStepChanged(int index, DrumHits hits)
    {
        if (index >= _pattern.Steps.Length || _pattern.Steps[index] == hits) return;
        _pattern = _pattern.WithStep(index, hits);
        PatternStatus = null;
        Publish();
    }

    /// <summary>While playing: outline the step being heard (ticks report every subdivision).</summary>
    private void ShowNowStep(int index)
    {
        var step = DrumBeats.SelectMany(b => b.Steps).FirstOrDefault(s => s.Index == index);
        if (step == _nowStep) return;
        if (_nowStep is not null) _nowStep.IsNow = false;
        _nowStep = step;
        if (step is not null) step.IsNow = true;
    }

    private void ClearNowStep()
    {
        if (_nowStep is not null) _nowStep.IsNow = false;
        _nowStep = null;
    }

    [RelayCommand]
    private void ClearSteps()
    {
        _pattern = _pattern with { Steps = new DrumHits[_pattern.StepCount] };
        RebuildDrumGrid();
        PatternStatus = "Cleared: click the cells to place each drum.";
        Publish();
    }

    [RelayCommand]
    private void SavePattern()
    {
        var name = (PatternName ?? "").Trim();
        if (name.Length == 0)
        {
            PatternStatus = "Give the pattern a name first.";
            return;
        }
        if (DrumPattern.BuiltIns.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            PatternStatus = $"“{name}” is a built-in pattern: save under another name.";
            return;
        }

        var saved = (_pattern with { Name = name }).Normalized();
        var library = LoadLibrary();
        var existing = library.FindIndex(p => string.Equals(p.Name, saved.Name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) library[existing] = saved;
        else library.Add(saved);
        if (!TrySaveLibrary(library)) return;

        _pattern = saved;
        PatternName = saved.Name;
        RefreshPatterns(saved.Name);
        PatternStatus = existing >= 0 ? $"Updated “{saved.Name}”." : $"Saved “{saved.Name}”.";
        Publish();
    }

    [RelayCommand(CanExecute = nameof(CanDeletePattern))]
    private void DeletePattern()
    {
        if (SelectedPattern is not { IsBuiltIn: false } option) return;
        if (!IsConfirmingDelete)
        {
            IsConfirmingDelete = true;
            return;
        }
        var library = LoadLibrary();
        library.RemoveAll(p => string.Equals(p.Name, option.Pattern.Name, StringComparison.OrdinalIgnoreCase));
        if (!TrySaveLibrary(library)) return;
        RefreshPatterns(selectName: null);
        IsConfirmingDelete = false;
        PatternStatus = $"Deleted “{option.Pattern.Name}”. The grid keeps it until you change it.";
    }

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    /// <summary>Fills the picker; selects <paramref name="selectName"/> without re-applying it (it's already current).</summary>
    private void RefreshPatterns(string? selectName)
    {
        Patterns.Clear();
        foreach (var p in DrumPattern.BuiltIns) Patterns.Add(new DrumPatternOption(p, IsBuiltIn: true));
        foreach (var p in LoadLibrary().OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            Patterns.Add(new DrumPatternOption(p, IsBuiltIn: false));

        var match = selectName is null ? null
            : Patterns.FirstOrDefault(o => string.Equals(o.Pattern.Name, selectName, StringComparison.OrdinalIgnoreCase));
        SetSelectedPatternQuietly(match);
    }

    /// <summary>The name of the built-in or saved pattern identical to the current grid, if any (else none is selected).</summary>
    private string? MatchingPatternName() =>
        DrumPattern.BuiltIns.Concat(LoadLibrary()).FirstOrDefault(p => p.Equals(_pattern))?.Name;

    private void SetSelectedPatternQuietly(DrumPatternOption? option)
    {
#pragma warning disable MVVMTK0034 // Deliberately bypass OnSelectedPatternChanged: the pattern is already current.
        _selectedPattern = option;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(SelectedPattern));
        OnPropertyChanged(nameof(CanDeletePattern));
        DeletePatternCommand.NotifyCanExecuteChanged();
        IsConfirmingDelete = false;
    }

    private List<DrumPattern> LoadLibrary()
    {
        try
        {
            return (_settingsStore.Get<DrumPatternLibrary>(PatternsKey)?.Patterns ?? [])
                .Where(p => p is not null).Select(p => p.Normalized()).Where(p => p.Name.Length > 0).ToList();
        }
#pragma warning disable CA1031 // A broken library must not break the metronome.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Loading drum patterns failed: {ex.Message}");
            return [];
        }
#pragma warning restore CA1031
    }

    private bool TrySaveLibrary(List<DrumPattern> library)
    {
        try
        {
            _settingsStore.Set(PatternsKey, new DrumPatternLibrary(library));
            return true;
        }
#pragma warning disable CA1031 // Shown, not thrown.
        catch (Exception ex)
        {
            PatternStatus = $"Couldn't save: {ex.Message}";
            return false;
        }
#pragma warning restore CA1031
    }
}

public sealed class DrumBeatViewModel(int number, IReadOnlyList<DrumStepViewModel> steps)
{
    public int Number { get; } = number;
    public IReadOnlyList<DrumStepViewModel> Steps { get; } = steps;
}

/// <summary>One step of the grid: which drums hit on it.</summary>
public sealed partial class DrumStepViewModel : ObservableObject
{
    private readonly MetronomeViewModel _owner;

    public DrumStepViewModel(MetronomeViewModel owner, int index, DrumHits hits, bool isOnBeat)
    {
        _owner = owner;
        Index = index;
        IsOnBeat = isOnBeat;
        _kick = (hits & DrumHits.Kick) != 0;
        _snare = (hits & DrumHits.Snare) != 0;
        _hiHat = (hits & DrumHits.HiHat) != 0;
    }

    public int Index { get; }

    /// <summary>The first step of a beat (drawn a little stronger).</summary>
    public bool IsOnBeat { get; }

    [ObservableProperty]
    private bool _kick;

    [ObservableProperty]
    private bool _snare;

    [ObservableProperty]
    private bool _hiHat;

    /// <summary>The step being heard right now.</summary>
    [ObservableProperty]
    private bool _isNow;

    public DrumHits Hits => (Kick ? DrumHits.Kick : 0) | (Snare ? DrumHits.Snare : 0) | (HiHat ? DrumHits.HiHat : 0);

    partial void OnKickChanged(bool value) => _owner.OnStepChanged(Index, Hits);
    partial void OnSnareChanged(bool value) => _owner.OnStepChanged(Index, Hits);
    partial void OnHiHatChanged(bool value) => _owner.OnStepChanged(Index, Hits);
}
