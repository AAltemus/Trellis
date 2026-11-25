using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using GuitarPractice.App.ViewModels;
using GuitarPractice.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace GuitarPractice.App;

internal sealed class ToolWindows(IServiceProvider services) : IToolWindows
{
    private readonly Dictionary<Type, Window> _open = [];

    public void ShowMetronome(MetronomeLink? link = null)
    {
        var window = Show<MetronomeWindow, MetronomeViewModel>();
        if (link is not null) (window.DataContext as MetronomeViewModel)?.Link(link);
    }

    public void ShowTuner() => _ = Show<TunerWindow, TunerViewModel>();

    public void ShowRecorder(RecorderLink? link = null, string? selectTake = null)
    {
        var window = Show<RecorderWindow, RecorderViewModel>();
        if (window.DataContext is not RecorderViewModel vm) return;
        if (link is not null) vm.OpenFor(link);
        if (selectTake is not null) vm.SelectTake(selectTake);
    }

    public void ShowEarTrainer() => _ = Show<EarTrainerWindow, EarTrainerViewModel>();

    public void ShowRhythmTrainer(RhythmTrainerLink? link = null)
    {
        var window = Show<RhythmTrainerWindow, RhythmTrainerViewModel>();
        if (link is not null) (window.DataContext as RhythmTrainerViewModel)?.OpenFor(link);
    }

    public void ShowSettings() => _ = Show<SettingsWindow, DesktopSettings>();

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
