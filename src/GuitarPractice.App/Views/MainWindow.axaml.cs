using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GuitarPractice.App.ViewModels;

namespace GuitarPractice.App.Views;

public partial class MainWindow : Window
{
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
}
