using System.Reflection;
using Avalonia.Threading;
using GuitarPractice.Infrastructure;
using GuitarPractice.Infrastructure.Persistence;

namespace GuitarPractice.App;

/// <summary>
/// Writes crashes to the diagnostics folder (<see cref="CrashLog"/>). Only records: whatever crashed the app still
/// crashes it, as before. The same exception seen by the dispatcher and then by the AppDomain is written once.
/// </summary>
internal static class CrashReporter
{
    private static Exception? _lastWritten;

    private static string AppVersion =>
        (typeof(CrashReporter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "dev").Split('+')[0];

    /// <summary>Background threads and anything that escapes the UI loop. Safe before Avalonia starts.</summary>
    public static void HookProcess()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Write(ex, "unhandled exception");
        };
        // Doesn't end the app (.NET ignores unobserved task exceptions), but means something failed silently.
        TaskScheduler.UnobservedTaskException += (_, e) => Write(e.Exception, "unobserved task exception");
    }

    /// <summary>Exceptions on the UI thread, caught by the dispatcher before they leave the platform's run loop.</summary>
    public static void HookDispatcher() =>
        Dispatcher.UIThread.UnhandledException += (_, e) => Write(e.Exception, "UI thread");

    public static string? Write(Exception exception, string source)
    {
        if (ReferenceEquals(exception, _lastWritten))
            return null;
        _lastWritten = exception;
        return CrashLog.Write(exception, source, AppPaths.DiagnosticsDirectory, DateTimeOffset.Now, AppVersion);
    }
}
