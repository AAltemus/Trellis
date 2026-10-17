using Avalonia.Controls;

namespace GuitarPractice.App.Views;

public partial class RecorderWindow : Window
{
    public RecorderWindow()
    {
        InitializeComponent();
        // Closing stops recording (the take is kept) and playback, releases the devices and saves settings.
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }
}
