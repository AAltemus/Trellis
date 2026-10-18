using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GuitarPractice.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
