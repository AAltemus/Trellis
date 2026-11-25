using System.Diagnostics;
using Avalonia;
using Avalonia.Styling;
using GuitarPractice.Infrastructure;

namespace GuitarPractice.App;

/// <summary>
/// Light or dark. There is deliberately no "follow the OS" option (user, 2026-10-18: "remove the system mode … only go with
/// light and dark. if it's cross platform i don't want anything platform specific"). Value 0 was "System": stored 0 → Light.
/// </summary>
public enum ThemeMode
{
    Light = 1,
    Dark = 2,
}

/// <summary>File → Settings → Appearance (AppSettings key "appearance").</summary>
public sealed record AppearanceSettings(ThemeMode Theme = ThemeMode.Light);

/// <summary>
/// Applies the theme app-wide (<see cref="Application.RequestedThemeVariant"/>, which every window inherits) and remembers
/// it. Always an explicit Light or Dark variant, never Avalonia's Default (which follows the OS).
/// </summary>
public sealed class ThemeService(SettingsStore settings)
{
    public const string SettingsKey = "appearance";

    public ThemeMode Current { get; private set; }

    /// <summary>At startup, before the first window: apply the remembered theme.</summary>
    public void ApplySaved()
    {
        AppearanceSettings? saved = null;
        try
        {
            saved = settings.Get<AppearanceSettings>(SettingsKey);
        }
#pragma warning disable CA1031 // A broken preference must never stop the app from starting.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Loading appearance settings failed: {ex.Message}");
        }
#pragma warning restore CA1031
        Apply(saved?.Theme == ThemeMode.Dark ? ThemeMode.Dark : ThemeMode.Light); // nothing saved, or an old "System" (0): Light
    }

    /// <summary>Switches every open window now and remembers the choice.</summary>
    public void Set(ThemeMode mode)
    {
        Apply(mode);
        try
        {
            settings.Set(SettingsKey, new AppearanceSettings(mode));
        }
#pragma warning disable CA1031 // Losing a preference must not break the switch itself.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Saving appearance settings failed: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    public static ThemeVariant VariantFor(ThemeMode mode) => mode == ThemeMode.Dark ? ThemeVariant.Dark : ThemeVariant.Light;

    private void Apply(ThemeMode mode)
    {
        Current = mode;
        if (Application.Current is { } app) app.RequestedThemeVariant = VariantFor(mode);
    }
}
