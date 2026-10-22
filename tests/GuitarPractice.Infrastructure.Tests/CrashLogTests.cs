namespace GuitarPractice.Infrastructure.Tests;

public sealed class CrashLogTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 22, 12, 0, 0, TimeSpan.FromHours(-4));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "trellis-crash-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static InvalidOperationException Thrown()
    {
        try
        {
            throw new InvalidOperationException("board item vanished", new ArgumentException("inner detail"));
        }
        catch (InvalidOperationException ex)
        {
            return ex; // thrown for real, so it carries a stack trace
        }
    }

    [Fact]
    public void Write_creates_the_folder_and_records_version_source_and_the_whole_exception()
    {
        var path = CrashLog.Write(Thrown(), "UI thread", Path.Combine(_root, "diagnostics"), Noon, "1.0.0");

        Assert.NotNull(path);
        Assert.Equal("crash-20261022-120000-000.txt", Path.GetFileName(path));
        var text = File.ReadAllText(path);
        Assert.StartsWith("Trellis 1.0.0 crashed (UI thread)", text, StringComparison.Ordinal);
        Assert.Contains("2026-10-22 12:00:00 -04:00", text, StringComparison.Ordinal);
        Assert.Contains("OS:", text, StringComparison.Ordinal);
        Assert.Contains("board item vanished", text, StringComparison.Ordinal);
        Assert.Contains("inner detail", text, StringComparison.Ordinal); // inner exceptions too
        Assert.Contains(nameof(Thrown), text, StringComparison.Ordinal); // and the stack trace
    }

    [Fact]
    public void Two_crashes_in_the_same_millisecond_get_separate_files()
    {
        var first = CrashLog.Write(Thrown(), "a", _root, Noon, "1.0.0");
        var second = CrashLog.Write(Thrown(), "b", _root, Noon, "1.0.0");

        Assert.NotEqual(first, second);
        Assert.Equal("crash-20261022-120000-000-2.txt", Path.GetFileName(second));
    }

    [Fact]
    public void Only_the_newest_files_are_kept_and_other_files_are_left_alone()
    {
        Directory.CreateDirectory(_root);
        var tunerWav = Path.Combine(_root, "tuner-20261001-080000.wav");
        File.WriteAllBytes(tunerWav, [1, 2, 3]);

        for (var i = 0; i < CrashLog.MaxFiles + 5; i++)
            CrashLog.Write(Thrown(), "test", _root, Noon.AddMinutes(i), "1.0.0");

        var crashes = Directory.GetFiles(_root, "crash-*.txt").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(CrashLog.MaxFiles, crashes.Count);
        Assert.Equal("crash-20261022-120500-000.txt", crashes[0]); // the five oldest are gone
        Assert.True(File.Exists(tunerWav));
    }

    [Fact]
    public void A_folder_that_cannot_be_created_returns_null_instead_of_throwing()
    {
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "diagnostics");
        File.WriteAllText(blocker, "a file where the folder should be");

        Assert.Null(CrashLog.Write(Thrown(), "test", blocker, Noon, "1.0.0"));
    }
}
