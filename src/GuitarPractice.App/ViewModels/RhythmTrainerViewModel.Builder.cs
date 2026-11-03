using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GuitarPractice.Domain.Rhythm;

namespace GuitarPractice.App.ViewModels;

/// <summary>A note or rest button of the rhythm builder (drawn as the symbol by <c>NoteGlyph</c>).</summary>
public sealed record BuildNote(NoteLength Length, bool IsRest, string Tip);

/// <summary>
/// The rhythm builder in "Your own rhythms": click notes and rests onto the end (Dotted and Triplet apply to the next
/// click), Tie, Undo, Clear, Listen. It writes <see cref="RhythmTrainerViewModel.OwnText"/> (the text box still works,
/// and a valid text loads back into the builder), so Add saves whichever you used.
/// </summary>
public sealed partial class RhythmTrainerViewModel
{
    private RhythmBuilder _builder = new(Meter.FourFour);
    private bool _writingText; // the builder is writing OwnText: don't load it back

    private static readonly (NoteLength Length, string Name)[] BuildLengths =
    [
        (NoteLength.Whole, "whole"), (NoteLength.Half, "half"), (NoteLength.Quarter, "quarter"),
        (NoteLength.Eighth, "8th"), (NoteLength.Sixteenth, "16th"), (NoteLength.ThirtySecond, "32nd"),
    ];

    public IReadOnlyList<BuildNote> BuildNotes { get; } =
        [.. BuildLengths.Select(l => new BuildNote(l.Length, false, $"Add a {l.Name} note"))];

    public IReadOnlyList<BuildNote> BuildRests { get; } =
        [.. BuildLengths.Select(l => new BuildNote(l.Length, true, $"Add a {l.Name} rest"))];

    /// <summary>The next note or rest is dotted (half as long again); turns itself off after one.</summary>
    [ObservableProperty]
    private bool _buildDotted;

    /// <summary>The next click adds three of that note in the time of two; turns itself off after one.</summary>
    [ObservableProperty]
    private bool _buildTriplet;

    /// <summary>What's been built so far (a short last bar, or the next empty bar once one is full).</summary>
    public RhythmPattern BuilderPattern => _builder.Preview;

    /// <summary>"Bar 2 of up to 4: 1.5 of 4 beats, 2.5 left".</summary>
    public string BuilderStatus
    {
        get
        {
            var b = _builder;
            var meter = b.Meter;
            if (b.IsEmpty) return $"Click notes and rests to fill a {meter.Signature} bar ({meter.BeatsText(meter.BarTicks)}).";
            if (b.LeftInBar == 0) return $"All {RhythmPattern.MaxBars} bars are full.";
            if (b.IsComplete)
                return $"{b.Bars} bar{(b.Bars == 1 ? "" : "s")}, complete{(b.IsLastTied ? " (but the last note can't be tied)" : "")}. " +
                       "Name it and Add, or keep going into the next bar.";
            return $"Bar {b.CurrentBar + 1} of up to {RhythmPattern.MaxBars}: {meter.BeatsText(b.UsedInBar)} of " +
                   $"{meter.BeatsText(meter.BarTicks)}, {meter.BeatsText(b.LeftInBar)} left.";
        }
    }

    [ObservableProperty]
    private double _builderPlayheadTick = -1;

    private bool CanAddNote(BuildNote? note) =>
        note is not null && !IsRunning && _builder.CanAdd(note.Length, BuildDotted, BuildTriplet);

    [RelayCommand(CanExecute = nameof(CanAddNote))]
    private void AddNote(BuildNote? note)
    {
        if (note is null || !_builder.Add(note.Length, BuildDotted, note.IsRest, BuildTriplet)) return;
        BuildDotted = false;
        BuildTriplet = false;
        BuilderChanged();
    }

    private bool CanTie() => !IsRunning && _builder.CanTie;

    public string TieText => _builder.IsLastTied ? "Untie" : "Tie →";

    /// <summary>Ties the last note to the next one (across a bar line too), or unties it.</summary>
    [RelayCommand(CanExecute = nameof(CanTie))]
    private void Tie()
    {
        _builder.ToggleTie();
        BuilderChanged();
    }

    private bool CanUndoOrClear() => !IsRunning && !_builder.IsEmpty;

    [RelayCommand(CanExecute = nameof(CanUndoOrClear))]
    private void UndoNote()
    {
        _builder.Undo();
        BuilderChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUndoOrClear))]
    private void ClearBuilder()
    {
        _builder.Clear();
        BuilderChanged();
    }

    private bool CanListen() => !IsRunning && _ownDraft is not null;

    /// <summary>Plays the rhythm being built once after a count-in (no scoring, nothing saved).</summary>
    [RelayCommand(CanExecute = nameof(CanListen))]
    private void Listen()
    {
        if (_ownDraft is { } draft) StartTake(draft, preview: true);
    }

    partial void OnBuildDottedChanged(bool value)
    {
        if (value) BuildTriplet = false; // a dotted triplet isn't offered
        AddNoteCommand.NotifyCanExecuteChanged();
    }

    partial void OnBuildTripletChanged(bool value)
    {
        if (value) BuildDotted = false;
        AddNoteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The builder changed: write its text (which checks it) and redraw.</summary>
    private void BuilderChanged()
    {
        _writingText = true;
        try
        {
            OwnText = _builder.Text;
        }
        finally
        {
            _writingText = false;
        }
        RefreshBuilder();
    }

    /// <summary>The text (or meter) changed elsewhere: load it into the builder if it reads as a (partial) rhythm;
    /// otherwise the builder keeps what it had (a mistake in the text is shown under it).</summary>
    private void LoadBuilder()
    {
        if (_writingText) return;
        if (RhythmPattern.TryParseDraft(SelectedMeter, OwnText, out var draft, out _)) _builder = RhythmBuilder.From(draft!);
        else if (!_builder.Meter.Equals(SelectedMeter)) _builder = new RhythmBuilder(SelectedMeter);
        RefreshBuilder();
    }

    private void RefreshBuilder()
    {
        OnPropertyChanged(nameof(BuilderPattern));
        OnPropertyChanged(nameof(BuilderStatus));
        OnPropertyChanged(nameof(TieText));
        NotifyBuilderCommands();
    }

    private void NotifyBuilderCommands()
    {
        AddNoteCommand.NotifyCanExecuteChanged();
        TieCommand.NotifyCanExecuteChanged();
        UndoNoteCommand.NotifyCanExecuteChanged();
        ClearBuilderCommand.NotifyCanExecuteChanged();
        ListenCommand.NotifyCanExecuteChanged();
    }
}
