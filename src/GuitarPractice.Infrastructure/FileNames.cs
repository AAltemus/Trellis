namespace GuitarPractice.Infrastructure;

/// <summary>
/// File-name rules that are the same on every OS: the characters Windows forbids (<c>\ / : * ? " &lt; &gt; |</c>) and
/// control characters. <see cref="System.IO.Path.GetInvalidFileNameChars"/> differs per OS (only <c>/</c> and NUL on
/// macOS/Linux), so a name accepted there could not be copied to Windows.
/// </summary>
public static class FileNames
{
    private const string Forbidden = "\\/:*?\"<>|";

    public static bool IsInvalid(char c) => c < ' ' || Forbidden.Contains(c, StringComparison.Ordinal);
}
