using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GuitarPractice.Infrastructure.Persistence;

namespace GuitarPractice.App.Views;

/// <summary>
/// Shown instead of the main window when startup fails (e.g. a database upgraded by a newer Trellis), so the reason is
/// readable and copyable instead of the app just vanishing. Built in code: it must work even if resources are broken.
/// </summary>
public sealed class StartupErrorWindow : Window
{
    public StartupErrorWindow(Exception error, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(error);
        Title = "Trellis couldn't start";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 80, IsDefault = true, IsCancel = true };
        close.Click += (_, _) => Close();
        var known = error is NewerDatabaseException;
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Trellis couldn't start", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new SelectableTextBlock { Text = error.Message, TextWrapping = TextWrapping.Wrap },
                new SelectableTextBlock
                {
                    Text = $"Your practice data is in {dataDirectory} and hasn't been changed.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.7,
                },
                new Expander
                {
                    Header = "Details",
                    IsVisible = !known,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = new SelectableTextBlock { Text = error.ToString(), TextWrapping = TextWrapping.Wrap, FontSize = 11 },
                },
                close,
            },
        };
    }
}
