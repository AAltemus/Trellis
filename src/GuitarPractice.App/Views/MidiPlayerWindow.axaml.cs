using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using GuitarPractice.App.ViewModels;

namespace GuitarPractice.App.Views;

public partial class MidiPlayerWindow : Window
{
    private static readonly FilePickerFileType MidiFiles = new("MIDI files") { Patterns = ["*.mid", "*.midi"] };

    public MidiPlayerWindow()
    {
        InitializeComponent();
        // Closing the window stops playback, saves the file's settings and releases the audio device.
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
        AddHandler(KeyDownEvent, OnShortcutKey, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MidiPlayerViewModel vm) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import MIDI files",
            AllowMultiple = true,
            FileTypeFilter = [MidiFiles, FilePickerFileTypes.All],
        });
        vm.ImportFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

#pragma warning disable CS0618 // DataFormats.Files / GetFiles: the Avalonia 11 drag-and-drop API.
    private static IEnumerable<string> DroppedMidiFiles(DragEventArgs e) =>
        (e.Data.GetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>()
            .Where(p => p.EndsWith(".mid", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".midi", StringComparison.OrdinalIgnoreCase));

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
#pragma warning restore CS0618

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is MidiPlayerViewModel vm) vm.ImportFiles(DroppedMidiFiles(e).ToList());
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
