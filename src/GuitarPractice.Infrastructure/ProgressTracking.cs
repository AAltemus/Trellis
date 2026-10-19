namespace GuitarPractice.Infrastructure;

/// <summary>What's left out of progress tracking (AppSettings key "progressTracking"): whole categories and single blocks.</summary>
public sealed record ProgressTrackingSettings(List<int>? ExcludedCategories = null, List<int>? ExcludedBlocks = null);

/// <summary>
/// Which blocks the progress tracker covers. Excluded ones (e.g. Warmup, Theory) don't show in the Progress window
/// and the timer panel offers no progress log for them; their history is kept and comes back when included again.
/// A block is excluded when it or its category is. Shared by the main window and Progress: <see cref="Changed"/> lets
/// each refresh.
/// </summary>
public sealed class ProgressTracking(SettingsStore settings)
{
    public const string SettingsKey = "progressTracking";

    public event EventHandler? Changed;

    public ProgressTrackingSettings Load() => settings.Get<ProgressTrackingSettings>(SettingsKey) ?? new ProgressTrackingSettings();

    public bool IsTracked(int categoryId, int subcategoryId)
    {
        var s = Load();
        return !(s.ExcludedCategories?.Contains(categoryId) ?? false) && !(s.ExcludedBlocks?.Contains(subcategoryId) ?? false);
    }

    public void SetCategory(int categoryId, bool tracked)
    {
        var s = Load();
        Save(s with { ExcludedCategories = Toggle(s.ExcludedCategories, categoryId, !tracked) });
    }

    public void SetBlock(int subcategoryId, bool tracked)
    {
        var s = Load();
        Save(s with { ExcludedBlocks = Toggle(s.ExcludedBlocks, subcategoryId, !tracked) });
    }

    private static List<int> Toggle(List<int>? excluded, int id, bool exclude) =>
        [.. (excluded ?? []).Where(x => x != id), .. exclude ? [id] : Array.Empty<int>()];

    private void Save(ProgressTrackingSettings s)
    {
        settings.Set(SettingsKey, s);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
