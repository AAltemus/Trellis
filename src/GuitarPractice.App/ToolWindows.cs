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
    void ShowMetronome();
    void ShowTuner();
    void ShowRecorder();
}

internal sealed class ToolWindows(IServiceProvider services) : IToolWindows
{
    private readonly Dictionary<Type, Window> _open = [];

    public void ShowMetronome() => Show<MetronomeWindow, MetronomeViewModel>();

    public void ShowTuner() => Show<TunerWindow, TunerViewModel>();

    public void ShowRecorder() => Show<RecorderWindow, RecorderViewModel>();

    private void Show<TWindow, TViewModel>()
        where TWindow : Window, new()
        where TViewModel : notnull
    {
        if (_open.TryGetValue(typeof(TWindow), out var existing))
        {
            existing.Activate();
            return;
        }

        var window = new TWindow { DataContext = services.GetRequiredService<TViewModel>() };
        window.Closed += (_, _) => _open.Remove(typeof(TWindow));
        _open[typeof(TWindow)] = window;

        // Owned by the main window: stays above it while you practise and closes with it.
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
            window.Show(owner);
        else
            window.Show();
    }
}
