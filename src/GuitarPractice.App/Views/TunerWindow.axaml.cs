using Avalonia.Controls;

namespace GuitarPractice.App.Views;

public partial class TunerWindow : Window
{
    public TunerWindow()
    {
        InitializeComponent();
        // Closing the window stops listening, releases the input and saves the tuner settings.
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }
}
