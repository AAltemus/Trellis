using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace GuitarPractice.Infrastructure;

/// <summary>
/// Writes an unhandled exception to a text file in the diagnostics folder (<c>crash-yyyyMMdd-HHmmss-fff.txt</c>), so a
/// crash someone can't reproduce still leaves a stack trace to send. Keeps the newest <see cref="MaxFiles"/> files.
/// Only runs when the app is already going down, so it never throws: a failed write returns null.
/// </summary>
public static class CrashLog
{
    public const int MaxFiles = 20;
    private const string Prefix = "crash-";

    /// <summary>Writes the report and returns its path, or null if it couldn't be written.</summary>
    public static string? Write(Exception exception, string source, string directory, DateTimeOffset now, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            Directory.CreateDirectory(directory);
            var path = UniquePath(directory, now);
            File.WriteAllText(path, Format(exception, source, now, appVersion), Encoding.UTF8);
            Prune(directory);
            return path;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static string Format(Exception exception, string source, DateTimeOffset now, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Trellis {appVersion} crashed ({source})");
        text.AppendLine(CultureInfo.InvariantCulture, $"Time:    {now:yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine(CultureInfo.InvariantCulture, $"OS:      {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        text.AppendLine(CultureInfo.InvariantCulture, $"Runtime: {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})");
        text.AppendLine();
        text.AppendLine(exception.ToString());
        return text.ToString();
    }

    private static string UniquePath(string directory, DateTimeOffset now)
    {
        var stem = Prefix + now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, stem + ".txt");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(directory, $"{stem}-{n}.txt");
        return path;
    }

    // Names sort by time, so the oldest come first.
    private static void Prune(string directory)
    {
        var files = Directory.GetFiles(directory, Prefix + "*.txt");
        Array.Sort(files, StringComparer.Ordinal);
        for (var i = 0; i < files.Length - MaxFiles; i++)
            File.Delete(files[i]);
    }
}
