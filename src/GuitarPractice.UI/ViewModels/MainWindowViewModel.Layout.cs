using CommunityToolkit.Mvvm.ComponentModel;

namespace GuitarPractice.App.ViewModels;

/// <summary>Which collapsible sections are open; remembered between sessions (AppSettings key "layout").</summary>
public sealed record LayoutSettings(bool LogExpanded = true, bool LibraryExpanded = true);

public sealed partial class MainWindowViewModel
{
    public const string LayoutSettingsKey = "layout";

    private bool _layoutLoaded;

    /// <summary>The "Log for &lt;day&gt;" section under the timer.</summary>
    [ObservableProperty]
    private bool _isLogExpanded = true;

    /// <summary>The library tree in the routine panel.</summary>
    [ObservableProperty]
    private bool _isLibraryExpanded = true;

    private void LoadLayout()
    {
        var saved = _settingsStore.Get<LayoutSettings>(LayoutSettingsKey) ?? new LayoutSettings();
        IsLogExpanded = saved.LogExpanded;
        IsLibraryExpanded = saved.LibraryExpanded;
        _layoutLoaded = true;
    }

    partial void OnIsLogExpandedChanged(bool value) => SaveLayout();

    partial void OnIsLibraryExpandedChanged(bool value) => SaveLayout();

    private void SaveLayout()
    {
        if (!_layoutLoaded) return;
        _settingsStore.Set(LayoutSettingsKey, new LayoutSettings(IsLogExpanded, IsLibraryExpanded));
    }
}
