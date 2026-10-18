using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using GuitarPractice.App.ViewModels;

namespace GuitarPractice.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Save a running timer's last seconds when the window closes.
        Closing += (_, _) => (DataContext as MainWindowViewModel)?.SaveOnExit();
        // Once the board is laid out (after the window's size is restored), scroll it so today is in view.
        Opened += (_, _) => Dispatcher.UIThread.Post(ScrollBoardToToday, DispatcherPriority.Background);
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
