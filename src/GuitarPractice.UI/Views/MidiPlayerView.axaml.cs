using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GuitarPractice.App.ViewModels;

namespace GuitarPractice.App.Views;

public partial class MidiPlayerView : UserControl
{
    // Patterns for desktop pickers, MIME types for pickers that filter by type (phones).
    private static readonly FilePickerFileType MidiFiles = new("MIDI files")
    {
        Patterns = ["*.mid", "*.midi"],
        MimeTypes = ["audio/midi", "audio/x-midi", "audio/mid"],
    };

    public MidiPlayerView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MidiPlayerViewModel vm)
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MidiPlayerViewModel.CurrentBar)) KeepBarInView(vm.CurrentBar);
                };
        };
    }

    /// <summary>Scrolls the bar map (only it, not the page) so the bar playing now is visible.</summary>
    private void KeepBarInView(int bar)
    {
        if (BarList.ContainerFromIndex(bar - 1) is not { } cell || cell.TranslatePoint(default, BarList) is not { } at) return;
        var top = at.Y;
        var bottom = top + cell.Bounds.Height;
        var offset = BarMap.Offset.Y;
        var height = BarMap.Viewport.Height;
        if (top < offset) BarMap.Offset = BarMap.Offset.WithY(top);
        else if (bottom > offset + height) BarMap.Offset = BarMap.Offset.WithY(bottom - height);
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MidiPlayerViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import MIDI files",
            AllowMultiple = true,
            FileTypeFilter = [MidiFiles, FilePickerFileTypes.All],
        });
        var local = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        var streamed = files.Where(f => f.TryGetLocalPath() is null).ToList();
        if (streamed.Count == 0)
        {
            vm.ImportFiles(local);
            return;
        }

        // Pickers that hand back content rather than a file path (phones): copy each into a temp folder under its own
        // name, import from there, then remove the copies.
        var temp = Directory.CreateTempSubdirectory("trellis-import-");
        try
        {
            foreach (var file in streamed)
            {
                var target = Path.Combine(temp.FullName, Path.GetFileName(file.Name));
                await using (var source = await file.OpenReadAsync())
                await using (var copy = File.Create(target))
                    await source.CopyToAsync(copy);
                local.Add(target);
            }
            vm.ImportFiles(local);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            vm.LibraryStatus = $"Couldn't read the file: {ex.Message}";
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static IEnumerable<string> DroppedMidiFiles(DragEventArgs e) =>
        (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>()
            .Where(p => p.EndsWith(".mid", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".midi", StringComparison.OrdinalIgnoreCase));

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is MidiPlayerViewModel vm) vm.ImportFiles(DroppedMidiFiles(e).ToList());
    }
}
