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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            CrashReporter.HookDispatcher();
            try
            {
                desktop.MainWindow = StartMainWindow(desktop);
            }
#pragma warning disable CA1031 // Whatever stops startup (a newer database, a broken install), show it instead of vanishing.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                CrashReporter.Write(ex, "startup");
                _host?.Dispose();
                _host = null;
                desktop.MainWindow = new StartupErrorWindow(ex, AppPaths.DataDirectory);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private MainWindow StartMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var host = _host = AppHost.Build();

        // Synchronous by design: must finish before the first window, and must not block on async
        // work under Avalonia's SynchronizationContext.
        host.Services.GetRequiredService<DatabaseInitializer>().Initialize();
        host.Services.GetRequiredService<ThemeService>().ApplySaved(); // before the first window: no flash of the wrong theme

        var mainVm = host.Services.GetRequiredService<MainWindowViewModel>();
        var updates = host.Services.GetRequiredService<UpdateViewModel>();
        updates.RequestExit = () => mainVm.ExitCommand.Execute(null); // "Update and restart" closes the normal way first
        var main = new MainWindow { DataContext = mainVm, Updates = updates };
        host.Services.GetRequiredService<WindowPlacementService>().Attach(main, "main"); // reopen at the last size and place
        // Tool windows still open close (and save) here, while the database services are alive. Disposing the host
        // on Exit also disposes every view model the container created; their Dispose runs only once, so a window
        // already closed doesn't try to save again then.
        main.Closing += (_, _) => host.Services.GetRequiredService<IToolWindows>().CloseAll();
        main.Opened += async (_, _) => await updates.CheckOnStartupAsync(); // installed copies only; never throws
        desktop.Exit += (_, _) => host.Dispose();
        return main;
    }
}
