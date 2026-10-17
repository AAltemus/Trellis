using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GuitarPractice.App.ViewModels;

namespace GuitarPractice.App.Views;

public partial class EarTrainerWindow : Window
{
    public EarTrainerWindow()
    {
        InitializeComponent();
        // Closing the window stops playback and releases the audio device.
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
        AddHandler(KeyDownEvent, OnShortcutKey, RoutingStrategies.Tunnel);
    }

    /// <summary>Enter/N next, Space/R replay, K key chord, 1–9 and 0 answer (not while a text box has focus).</summary>
    private void OnShortcutKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not EarTrainerViewModel vm || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Source is Visual source && (source as TextBox ?? source.FindAncestorOfType<TextBox>()) is not null) return;
        // An open drop-down owns the keyboard.
        if (e.Source is Visual v && (v as ComboBox ?? v.FindAncestorOfType<ComboBox>()) is { IsDropDownOpen: true }) return;

        switch (e.Key)
        {
            case Key.Enter or Key.N:
                if (vm.NextCommand.CanExecute(null)) vm.NextCommand.Execute(null);
                break;
            case Key.Space or Key.R:
                if (vm.ReplayCommand.CanExecute(null)) vm.ReplayCommand.Execute(null);
                break;
            case Key.K:
                if (vm.PlayKeyCommand.CanExecute(null)) vm.PlayKeyCommand.Execute(null);
                break;
            case >= Key.D0 and <= Key.D9:
                vm.ChooseNumber(e.Key - Key.D0);
                break;
            case >= Key.NumPad0 and <= Key.NumPad9:
                vm.ChooseNumber(e.Key - Key.NumPad0);
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
