using CommunityToolkit.Mvvm.ComponentModel;

namespace GuitarPractice.App.ViewModels;

/// <summary>
/// File → Settings. Changes apply immediately and are remembered (no OK/Cancel). Starts with Appearance; later settings
/// get their own section here.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ThemeService _theme;

    public SettingsViewModel(ThemeService theme, UpdateViewModel updates)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(updates);
        _theme = theme;
        Updates = updates;
        _isLight = theme.Current == ThemeMode.Light;
        _isDark = theme.Current == ThemeMode.Dark;
    }

    /// <summary>Version, check-on-startup and Check now (shared with the main window's update banner).</summary>
    public UpdateViewModel Updates { get; }

    // One bool per radio button (Avalonia radio buttons bind IsChecked); only the one turned on acts.

    [ObservableProperty]
    private bool _isLight;

    [ObservableProperty]
    private bool _isDark;

    partial void OnIsLightChanged(bool value)
    {
        if (value) _theme.Set(ThemeMode.Light);
    }

    partial void OnIsDarkChanged(bool value)
    {
        if (value) _theme.Set(ThemeMode.Dark);
    }
}
