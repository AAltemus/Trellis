using Avalonia;
using Velopack;

namespace GuitarPractice.App;

internal static class Program
{
    // Don't touch Avalonia, third-party APIs or SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static void Main(string[] args)
    {
        // First: lets Velopack's installer/updater run its install/update/uninstall hooks (they exit straight away).
        // A build run from VS Code isn't installed, so this returns at once.
        VelopackApp.Build().Run();
        CrashReporter.HookProcess(); // crash reports in the diagnostics folder
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
