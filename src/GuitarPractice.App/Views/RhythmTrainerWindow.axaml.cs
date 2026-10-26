using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GuitarPractice.App.ViewModels;

namespace GuitarPractice.App.Views;

public partial class RhythmTrainerWindow : Window
{
    public RhythmTrainerWindow()
    {
        InitializeComponent();
        // Closing the window stops the take and releases the audio device.
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
        AddHandler(KeyDownEvent, OnShortcutKey, RoutingStrategies.Tunnel);
    }

    /// <summary>Space starts/stops a take. Not while a text box has focus or a drop-down is open.</summary>
    private void OnShortcutKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not RhythmTrainerViewModel vm || e.KeyModifiers != KeyModifiers.None || e.Key != Key.Space) return;
        if (e.Source is Visual source && (source as TextBox ?? source.FindAncestorOfType<TextBox>()) is not null) return;
        if (e.Source is Visual v && (v as ComboBox ?? v.FindAncestorOfType<ComboBox>()) is { IsDropDownOpen: true }) return;
        if (vm.StartStopCommand.CanExecute(null)) vm.StartStopCommand.Execute(null);
        e.Handled = true;
    }
}
