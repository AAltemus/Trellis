using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GuitarPractice.App.ViewModels;

namespace GuitarPractice.App.Views;

public partial class MainWindow : Window
{
    /// <summary>A board item being dragged, inside this app only.</summary>
    private static readonly DataFormat<EntryViewModel> EntryFormat = DataFormat.CreateInProcessFormat<EntryViewModel>("trellis-board-entry");

    /// <summary>Pointer distance before a press on an item becomes a drag (a plain click still selects it).</summary>
    private const double DragThreshold = 6;

    private PointerPressedEventArgs? _press;
    private EntryViewModel? _pressed;
    private Point _pressedAt;
    private Button? _hovered;

    public MainWindow()
    {
        InitializeComponent();
        // The update banner has its own view model; until the app sets one it stays empty (and hidden), never the
        // window's MainWindowViewModel by inheritance.
        UpdateBar.DataContext = null;
        // Save a running timer's last seconds when the window closes.
        Closing += (_, _) => (DataContext as MainWindowViewModel)?.SaveOnExit();
        // Once the board is laid out (after the window's size is restored), scroll it so today is in view.
        Opened += (_, _) => Dispatcher.UIThread.Post(ScrollBoardToToday, DispatcherPriority.Background);

        // Swapping board items by drag and drop. Tunnel + handledEventsToo: the item buttons handle their own presses.
        AddHandler(PointerPressedEvent, OnBoardPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnBoardPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => _press = null, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnEntryDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => Highlight(null, ok: false));
        AddHandler(DragDrop.DropEvent, OnEntryDrop);
    }

    /// <summary>The shared update banner / Help → Check for updates state.</summary>
    public UpdateViewModel? Updates
    {
        get => UpdateBar.DataContext as UpdateViewModel;
        set => UpdateBar.DataContext = value;
    }

    private void OnCheckForUpdates(object? sender, RoutedEventArgs e)
    {
        if (Updates?.CheckNowCommand is { } check && check.CanExecute(null)) check.Execute(null);
    }

    private void ScrollBoardToToday()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        var scrollable = WeekBoard.Extent.Width - WeekBoard.Viewport.Width;
        if (scrollable > 0)
            WeekBoard.Offset = new Vector(scrollable * vm.TodayScrollFraction, WeekBoard.Offset.Y);
    }

    // ---- Swapping items: drag one onto another ----

    private static Button? EntryButton(object? source) =>
        (source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is { DataContext: EntryViewModel } b && b.Classes.Contains("entry")
            ? b
            : null;

    private void OnBoardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _press = null;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || EntryButton(e.Source) is not { DataContext: EntryViewModel entry }) return;
        _press = e;
        _pressed = entry;
        _pressedAt = e.GetPosition(this);
    }

    private async void OnBoardPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || _pressed is not { } entry) return;
        var at = e.GetPosition(this);
        if (Math.Abs(at.X - _pressedAt.X) < DragThreshold && Math.Abs(at.Y - _pressedAt.Y) < DragThreshold) return;
        _press = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(EntryFormat, entry));
        await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move);
        Highlight(null, ok: false);
    }

    private void OnEntryDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        if (DataContext is not MainWindowViewModel vm || e.DataTransfer.TryGetValue(EntryFormat) is not { } source) return;
        if (EntryButton(e.Source) is not { DataContext: EntryViewModel target } button || target.Key == source.Key)
        {
            Highlight(null, ok: false);
            return;
        }
        var why = vm.WhyNotSwap(source, target);
        e.DragEffects = why is null ? DragDropEffects.Move : DragDropEffects.None;
        Highlight(button, ok: why is null);
        vm.BoardMessage = why ?? $"Drop to swap {source.Name} ({source.PlannedMinutes} min) with {target.Name} ({target.PlannedMinutes} min).";
    }

    private void OnEntryDrop(object? sender, DragEventArgs e)
    {
        Highlight(null, ok: false);
        if (DataContext is not MainWindowViewModel vm || e.DataTransfer.TryGetValue(EntryFormat) is not { } source) return;
        if (EntryButton(e.Source) is { DataContext: EntryViewModel target } && target.Key != source.Key) vm.Swap(source, target);
    }

    private void Highlight(Button? button, bool ok)
    {
        if (_hovered is not null && !ReferenceEquals(_hovered, button)) _hovered.Classes.Remove("droptarget");
        if (_hovered is not null && !ReferenceEquals(_hovered, button)) _hovered.Classes.Remove("dropblocked");
        _hovered = button;
        if (button is null) return;
        button.Classes.Set("droptarget", ok);
        button.Classes.Set("dropblocked", !ok);
    }

    // ---- Swapping items: right-click → Swap with… ----

    /// <summary>
    /// Fills "Swap with" when the menu has opened: only then does it carry the item as its DataContext (during Opening
    /// it has none yet). The submenu unfolds after this, so it's always filled in time.
    /// </summary>
    private void OnEntryMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { DataContext: EntryViewModel entry } menu || DataContext is not MainWindowViewModel vm
            || menu.Items.Count == 0 || menu.Items[0] is not MenuItem swapWith) return;
        swapWith.ItemsSource = vm.SwapTargets(entry)
            .Select(t => new MenuItem { Header = t.Header, IsEnabled = t.IsEnabled, Command = t.Command })
            .ToList();
    }
}
