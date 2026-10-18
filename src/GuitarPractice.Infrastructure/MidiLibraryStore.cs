using GuitarPractice.Infrastructure.Persistence;

namespace GuitarPractice.Infrastructure;

/// <summary>A MIDI file in the library: its file name (the identity), display name and size.</summary>
public sealed record MidiLibraryItem(string FileName, string Path, long Bytes, DateTime Modified)
{
    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(FileName);
}

/// <summary>
/// How a file was last played, restored when it's opened again. Tracks are identified by their index in the file, so
/// muting the guitar sticks even if tracks are added elsewhere. <see cref="TrackVolumes"/>: file track index → percent
/// (0–100), only for tracks not at 100. Fields are only ever appended (older saved rows load with the defaults).
/// </summary>
public sealed record MidiFileSettings(
    int SpeedPercent = 100,
    bool LoopEnabled = false,
    int LoopFromBar = 1,
    int LoopToBar = 4,
    bool CountIn = true,
    int VolumePercent = 100,
    List<int>? MutedTracks = null,
    List<int>? SoloTracks = null,
    bool CountInEachLoop = false,
    Dictionary<int, int>? TrackVolumes = null);

/// <summary>Per-file settings and practice-item links (AppSettings key "midiLibrary").</summary>
public sealed record MidiLibraryData(Dictionary<string, MidiFileSettings> Files, Dictionary<int, string> Links);

/// <summary>
/// The MIDI player's library: imported files are copied into <c>%LOCALAPPDATA%\GuitarPractice\midi</c> so they stay
/// available after the original (e.g. a Guitar Pro export) moves. Keeps each file's playback settings and which practice
/// item (subcategory) each file is linked to. Synchronous, like the other stores.
/// </summary>
public sealed class MidiLibraryStore(SettingsStore settings, string? directory = null)
{
    public const string SettingsKey = "midiLibrary";
    public static readonly string[] Extensions = [".mid", ".midi"];

    public string Directory { get; } = directory ?? System.IO.Path.Combine(AppPaths.DataDirectory, "midi");

    /// <summary>The files in the library, by name.</summary>
    public IReadOnlyList<MidiLibraryItem> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        return new DirectoryInfo(Directory).EnumerateFiles()
            .Where(f => Extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(f => new MidiLibraryItem(f.Name, f.FullName, f.Length, f.LastWriteTime))
            .ToList();
    }

    public MidiLibraryItem? Find(string fileName) =>
        List().FirstOrDefault(i => string.Equals(i.FileName, fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Copies a file into the library; a name already taken gets " (2)", " (3)" … Returns the new item.</summary>
    public MidiLibraryItem Import(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var extension = System.IO.Path.GetExtension(sourcePath);
        if (!Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Not a MIDI file name (.mid or .midi): {System.IO.Path.GetFileName(sourcePath)}", nameof(sourcePath));
        System.IO.Directory.CreateDirectory(Directory);
        var target = UniquePath(System.IO.Path.GetFileNameWithoutExtension(sourcePath), extension.ToLowerInvariant());
        File.Copy(sourcePath, target);
        return Find(System.IO.Path.GetFileName(target))!;
    }

    /// <summary>Renames a file (keeps its extension); its settings and links follow. Returns the renamed item.</summary>
    public MidiLibraryItem Rename(string fileName, string newName)
    {
        var item = Find(fileName) ?? throw new FileNotFoundException("Not in the library.", fileName);
        var clean = CleanName(newName);
        if (clean.Length == 0) throw new ArgumentException("Give it a name.", nameof(newName));
        var newFileName = clean + System.IO.Path.GetExtension(item.FileName);
        if (string.Equals(newFileName, item.FileName, StringComparison.Ordinal)) return item;
        var conflict = List().FirstOrDefault(i => string.Equals(i.DisplayName, clean, StringComparison.OrdinalIgnoreCase));
        if (conflict is not null && !string.Equals(conflict.FileName, item.FileName, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"“{clean}” is already in the library.");

        File.Move(item.Path, System.IO.Path.Combine(Directory, newFileName));
        var data = Load();
        if (Take(data.Files, item.FileName) is { } fileSettings) data.Files[newFileName] = fileSettings;
        foreach (var link in data.Links.Where(l => string.Equals(l.Value, item.FileName, StringComparison.OrdinalIgnoreCase)).ToList())
            data.Links[link.Key] = newFileName;
        Save(data);
        return Find(newFileName)!;
    }

    /// <summary>Deletes the library copy (the original you imported from is untouched), its settings and links.</summary>
    public void Delete(string fileName)
    {
        if (Find(fileName) is { } item) File.Delete(item.Path);
        var data = Load();
        Take(data.Files, fileName);
        foreach (var link in data.Links.Where(l => string.Equals(l.Value, fileName, StringComparison.OrdinalIgnoreCase)).ToList())
            data.Links.Remove(link.Key);
        Save(data);
    }

    public MidiFileSettings GetSettings(string fileName) =>
        Load().Files.FirstOrDefault(f => string.Equals(f.Key, fileName, StringComparison.OrdinalIgnoreCase)).Value ?? new MidiFileSettings();

    public void SaveSettings(string fileName, MidiFileSettings fileSettings)
    {
        ArgumentNullException.ThrowIfNull(fileSettings);
        var data = Load();
        Take(data.Files, fileName);
        data.Files[fileName] = fileSettings;
        Save(data);
    }

    /// <summary>The file linked to a practice item, if it is still in the library.</summary>
    public MidiLibraryItem? LinkedFile(int subcategoryId) =>
        Load().Links.TryGetValue(subcategoryId, out var name) ? Find(name) : null;

    public void Link(int subcategoryId, string fileName)
    {
        var data = Load();
        data.Links[subcategoryId] = fileName;
        Save(data);
    }

    public void Unlink(int subcategoryId)
    {
        var data = Load();
        if (data.Links.Remove(subcategoryId)) Save(data);
    }

    /// <summary>Practice items linked to <paramref name="fileName"/>.</summary>
    public IReadOnlyList<int> LinksTo(string fileName) =>
        [.. Load().Links.Where(l => string.Equals(l.Value, fileName, StringComparison.OrdinalIgnoreCase)).Select(l => l.Key).Order()];

    /// <summary>Drops characters Windows doesn't allow in file names ("AC/DC" → "ACDC") and trims; keeps the rest as typed.</summary>
    public static string CleanName(string? name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var clean = new string((name ?? "").Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');
        return clean.Length > 120 ? clean[..120].TrimEnd() : clean;
    }

    private string UniquePath(string baseName, string extension)
    {
        var name = CleanName(baseName);
        if (name.Length == 0) name = "Untitled";
        // Unique by display name: "Song.mid" and "Song.midi" would both show as "Song".
        var taken = List().Select(i => i.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = name;
        for (var n = 2; taken.Contains(candidate); n++) candidate = $"{name} ({n})";
        return System.IO.Path.Combine(Directory, candidate + extension);
    }

    private static MidiFileSettings? Take(Dictionary<string, MidiFileSettings> files, string fileName)
    {
        var key = files.Keys.FirstOrDefault(k => string.Equals(k, fileName, StringComparison.OrdinalIgnoreCase));
        if (key is null) return null;
        var value = files[key];
        files.Remove(key);
        return value;
    }

    private MidiLibraryData Load()
    {
        var data = settings.Get<MidiLibraryData>(SettingsKey);
        return new MidiLibraryData(data?.Files ?? [], data?.Links ?? []);
    }

    private void Save(MidiLibraryData data) => settings.Set(SettingsKey, data);
}
