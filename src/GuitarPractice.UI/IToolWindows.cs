using GuitarPractice.App.ViewModels;

namespace GuitarPractice.App;

/// <summary>Opens the tools: on desktop each in its own window (one of each; asking again brings it to the front), on a
/// phone as a page.</summary>
public interface IToolWindows
{
    /// <summary>Opens the metronome; with a <paramref name="link"/>, plays for that practice item.</summary>
    void ShowMetronome(MetronomeLink? link = null);
    void ShowTuner();
    /// <summary>Opens the Recorder; with a <paramref name="link"/>, new takes attach to that item and day; <paramref name="selectTake"/> = a take file name to select.</summary>
    void ShowRecorder(RecorderLink? link = null, string? selectTake = null);
    void ShowEarTrainer();
    /// <summary>Opens the Rhythm Trainer; with a <paramref name="link"/>, Log score goes to that practice item and day.</summary>
    void ShowRhythmTrainer(RhythmTrainerLink? link = null);
    void ShowSettings();

    /// <summary>Opens the MIDI player; with a <paramref name="link"/>, for that practice item (its linked file opens).</summary>
    void ShowMidiPlayer(MidiPlayerLink? link = null);

    /// <summary>Opens Progress, selecting <paramref name="subcategoryId"/> when given.</summary>
    void ShowProgress(int? subcategoryId = null);

    /// <summary>If the Progress window is open, switch it to this exercise (never opens it; board → Progress only).</summary>
    void FollowInProgress(int subcategoryId);

    /// <summary>
    /// Closes every open tool window (each saves its settings and size/position). Called while the main window closes,
    /// so it happens before the app host (database access) is disposed.
    /// </summary>
    void CloseAll();
}
