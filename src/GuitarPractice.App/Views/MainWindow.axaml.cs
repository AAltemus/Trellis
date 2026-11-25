using Avalonia.Controls;
using Avalonia.Interactivity;
using GuitarPractice.App.ViewModels;

namespace GuitarPractice.App.Views;

/// <summary>Desktop shell around <see cref="BoardView"/>: menu, keyboard shortcuts and the update banner.</summary>
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
}
