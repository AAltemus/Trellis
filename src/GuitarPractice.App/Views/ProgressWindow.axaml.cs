using Avalonia.Controls;

namespace GuitarPractice.App.Views;

public partial class ProgressWindow : Window
{
    public ProgressWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose(); // unsubscribes from store changes
    }
}
