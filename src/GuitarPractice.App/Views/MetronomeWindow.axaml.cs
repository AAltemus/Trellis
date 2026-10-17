using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GuitarPractice.App.ViewModels;

namespace GuitarPractice.App.Views;

public partial class MetronomeWindow : Window
{
    public MetronomeWindow()
    {
        InitializeComponent();
        // Closing the window stops the click and releases the audio device.
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
        // Tunnel: seen before any control, so shortcuts work wherever focus is, except while typing in a text box
        // (pattern name, trainer target), where Space, T and the arrows belong to the text.
        AddHandler(KeyDownEvent, OnShortcutKey, RoutingStrategies.Tunnel);
    }

    private void OnShortcutKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MetronomeViewModel vm || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Source is Visual source && (source as TextBox ?? source.FindAncestorOfType<TextBox>()) is not null) return;

        switch (e.Key)
        {
            case Key.Space: vm.StartStopCommand.Execute(null); break;
            case Key.Up: vm.AdjustBpmCommand.Execute("1"); break;
            case Key.Down: vm.AdjustBpmCommand.Execute("-1"); break;
            case Key.PageUp: vm.AdjustBpmCommand.Execute("5"); break;
            case Key.PageDown: vm.AdjustBpmCommand.Execute("-5"); break;
            case Key.T: vm.TapCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }
}
