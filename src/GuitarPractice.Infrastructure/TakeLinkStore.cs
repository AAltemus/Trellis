namespace GuitarPractice.Infrastructure;

/// <summary>What a Recorder take belongs to: a practice block on a day, optionally one exercise in it ("3nps scale").</summary>
public sealed record TakeLink(int SubcategoryId, DateOnly Date, string? Exercise = null);

/// <summary>
/// Links Recorder takes (WAV files in the recordings folder, by file name) to practice blocks, days and exercises
/// (AppSettings key "takeLinks"; no DB migration). The Recorder keeps it in step when a take is renamed or deleted.
/// </summary>
public sealed class TakeLinkStore(SettingsStore settings)
{
    public const string SettingsKey = "takeLinks";

    /// <summary>Raised after any change, so the Recorder, Progress and the timer panel refresh.</summary>
    public event EventHandler? Changed;

    public TakeLink? Get(string fileName) =>
        Load().GetValueOrDefault(Key(fileName));

    public void Set(string fileName, TakeLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var all = Load();
        all[Key(fileName)] = link with { Exercise = string.IsNullOrWhiteSpace(link.Exercise) ? null : link.Exercise.Trim() };
        Save(all);
    }

    public void Remove(string fileName)
    {
        var all = Load();
        if (all.Remove(Key(fileName))) Save(all);
    }

    /// <summary>The take was renamed: its link follows.</summary>
    public void Rename(string oldFileName, string newFileName)
    {
        var all = Load();
        if (!all.Remove(Key(oldFileName), out var link)) return;
        all[Key(newFileName)] = link;
        Save(all);
    }

    /// <summary>Take file names linked to a block on a day (oldest name first).</summary>
    public IReadOnlyList<string> For(int subcategoryId, DateOnly date) =>
        [.. Load().Where(kv => kv.Value.SubcategoryId == subcategoryId && kv.Value.Date == date).Select(kv => kv.Key).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>How many takes each day of a block has.</summary>
    public IReadOnlyDictionary<DateOnly, int> CountsByDay(int subcategoryId) =>
        Load().Values.Where(l => l.SubcategoryId == subcategoryId).GroupBy(l => l.Date).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>
    /// The file name after the last '/' or '\', on every OS (Path.GetFileName ignores '\' on macOS/Linux; names users
    /// type can't contain either, see <see cref="FileNames.IsInvalid"/>).
    /// </summary>
    private static string Key(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return fileName[(fileName.LastIndexOfAny(['/', '\\']) + 1)..];
    }

    private Dictionary<string, TakeLink> Load() =>
        settings.Get<Dictionary<string, TakeLink>>(SettingsKey) is { } d
            ? new Dictionary<string, TakeLink>(d, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, TakeLink>(StringComparer.OrdinalIgnoreCase);

    private void Save(Dictionary<string, TakeLink> all)
    {
        settings.Set(SettingsKey, all);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
