using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GuitarPractice.App.ViewModels;

namespace GuitarPractice.App.Views;

/// <summary>Desktop host for <see cref="MidiPlayerView"/> (import, file drop and the bar map live there).</summary>
public partial class MidiPlayerWindow : Window
{
    public MidiPlayerWindow()
    {
        InitializeComponent();
        // Closing the window stops playback, saves the file's settings and releases the audio device.
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
        AddHandler(KeyDownEvent, OnShortcutKey, RoutingStrategies.Tunnel);
    }

    /// <summary>Space play/pause, Home back to the start (not while a text box has focus).</summary>
    private void OnShortcutKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MidiPlayerViewModel vm || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Source is Visual source && (source as TextBox ?? source.FindAncestorOfType<TextBox>()) is not null) return;

        switch (e.Key)
        {
            case Key.Space when vm.PlayPauseCommand.CanExecute(null):
                vm.PlayPauseCommand.Execute(null);
                break;
            case Key.Home when vm.StopCommand.CanExecute(null):
                vm.StopCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
