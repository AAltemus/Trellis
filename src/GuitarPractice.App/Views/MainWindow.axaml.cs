using Avalonia.Controls;
using GuitarPractice.App.ViewModels;

namespace GuitarPractice.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Save a running timer's last seconds when the window closes.
        Closing += (_, _) => (DataContext as MainWindowViewModel)?.SaveOnExit();
    }
}
