using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GuitarPractice.App.ViewModels;
using GuitarPractice.App.Views;
using GuitarPractice.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GuitarPractice.App;

public partial class App : Application
{
    private IHost? _host;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        _host = AppHost.Build();

        // Synchronous by design: must finish before the first window, and must not block on async
        // work under Avalonia's SynchronizationContext.
        _host.Services.GetRequiredService<DatabaseInitializer>().Initialize();
        _host.Services.GetRequiredService<ThemeService>().ApplySaved(); // before the first window: no flash of the wrong theme

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main = new MainWindow
            {
                DataContext = _host.Services.GetRequiredService<MainWindowViewModel>(),
            };
            _host.Services.GetRequiredService<WindowPlacementService>().Attach(main, "main"); // reopen at the last size and place
            desktop.MainWindow = main;
            desktop.Exit += (_, _) => _host.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
