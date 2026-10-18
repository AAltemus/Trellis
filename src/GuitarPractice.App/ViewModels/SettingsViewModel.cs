using CommunityToolkit.Mvvm.ComponentModel;

namespace GuitarPractice.App.ViewModels;

/// <summary>
/// File → Settings. Changes apply immediately and are remembered (no OK/Cancel). Starts with Appearance; later settings
/// get their own section here.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ThemeService _theme;

    public SettingsViewModel(ThemeService theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        _theme = theme;
        _isLight = theme.Current == ThemeMode.Light;
        _isDark = theme.Current == ThemeMode.Dark;
    }

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
