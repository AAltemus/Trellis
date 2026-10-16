using Avalonia.Controls;

namespace GuitarPractice.App.Views;

public partial class MetronomeWindow : Window
{
    public MetronomeWindow()
    {
        InitializeComponent();
        // Closing the window stops the click and releases the audio device.
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }
}
