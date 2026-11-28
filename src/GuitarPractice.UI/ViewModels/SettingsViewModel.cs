using CommunityToolkit.Mvvm.ComponentModel;
using GuitarPractice.Infrastructure;

namespace GuitarPractice.App.ViewModels;

/// <summary>File → Settings → Board (AppSettings key "board").</summary>
/// <param name="AutoAdvance">When an item finishes (zero reached or Stop), select the next unfinished item on that day
/// (never started automatically). Off: the finished item stays selected.</param>
public sealed record BoardSettings(bool AutoAdvance = true)
{
    public const string SettingsKey = "board";
}

/// <summary>
/// File → Settings. Changes apply immediately and are remembered (no OK/Cancel). Appearance and Board here; the desktop
/// window adds its Updates section (<c>DesktopSettings</c> in the desktop app), which phones don't have (store updates).
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ThemeService _theme;
    private readonly SettingsStore _settings;

    public SettingsViewModel(ThemeService theme, SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(settings);
        _theme = theme;
        _settings = settings;
        _isLight = theme.Current == ThemeMode.Light;
        _isDark = theme.Current == ThemeMode.Dark;
        _autoAdvance = (settings.Get<BoardSettings>(BoardSettings.SettingsKey) ?? new BoardSettings()).AutoAdvance;
    }

    // One bool per radio button (Avalonia radio buttons bind IsChecked); only the one turned on acts.

    [ObservableProperty]
    private bool _isLight;

    [ObservableProperty]
    private bool _isDark;

    /// <summary>Board: move on to the next item when one finishes (default on).</summary>
    [ObservableProperty]
    private bool _autoAdvance;

    partial void OnIsLightChanged(bool value)
    {
        if (value) _theme.Set(ThemeMode.Light);
    }

    partial void OnIsDarkChanged(bool value)
    {
        if (value) _theme.Set(ThemeMode.Dark);
    }

    partial void OnAutoAdvanceChanged(bool value) => _settings.Set(BoardSettings.SettingsKey, new BoardSettings(value));
}
