using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using GuitarPractice.App.ViewModels;
using GuitarPractice.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace GuitarPractice.App;

/// <summary>Opens the Tools-menu windows. One instance of each; asking again brings it to the front.</summary>
public interface IToolWindows
{
    /// <summary>Opens the metronome; with a <paramref name="link"/>, plays for that practice item.</summary>
    void ShowMetronome(MetronomeLink? link = null);
    void ShowTuner();
    void ShowRecorder();
    void ShowEarTrainer();
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

internal sealed class ToolWindows(IServiceProvider services) : IToolWindows
{
    private readonly Dictionary<Type, Window> _open = [];

    public void ShowMetronome(MetronomeLink? link = null)
    {
        var window = Show<MetronomeWindow, MetronomeViewModel>();
        if (link is not null) (window.DataContext as MetronomeViewModel)?.Link(link);
    }

    public void ShowTuner() => _ = Show<TunerWindow, TunerViewModel>();

    public void ShowRecorder() => _ = Show<RecorderWindow, RecorderViewModel>();

    public void ShowEarTrainer() => _ = Show<EarTrainerWindow, EarTrainerViewModel>();

    public void ShowSettings() => _ = Show<SettingsWindow, SettingsViewModel>();

    public void ShowMidiPlayer(MidiPlayerLink? link = null)
    {
        var window = Show<MidiPlayerWindow, MidiPlayerViewModel>();
        if (link is not null) (window.DataContext as MidiPlayerViewModel)?.OpenFor(link);
    }

    public void ShowProgress(int? subcategoryId = null)
    {
        var window = Show<ProgressWindow, ProgressViewModel>();
        (window.DataContext as ProgressViewModel)?.Select(subcategoryId);
    }

    public void FollowInProgress(int subcategoryId)
    {
        if (_open.TryGetValue(typeof(ProgressWindow), out var window))
            (window.DataContext as ProgressViewModel)?.Select(subcategoryId);
    }

    public void CloseAll()
    {
        foreach (var window in _open.Values.ToList()) window.Close(); // Closed removes it from _open
    }

    private Window Show<TWindow, TViewModel>()
        where TWindow : Window, new()
        where TViewModel : notnull
    {
        if (_open.TryGetValue(typeof(TWindow), out var existing))
        {
            existing.Activate();
            return existing;
        }

        var window = new TWindow { DataContext = services.GetRequiredService<TViewModel>() };
        services.GetRequiredService<WindowPlacementService>().Attach(window, typeof(TWindow).Name); // last size and place
        window.Closed += (_, _) => _open.Remove(typeof(TWindow));
        _open[typeof(TWindow)] = window;

        // Owned by the main window: stays above it while you practise and closes with it.
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
            window.Show(owner);
        else
            window.Show();
        return window;
    }
}
