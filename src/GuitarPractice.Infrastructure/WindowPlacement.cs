namespace GuitarPractice.Infrastructure;

/// <summary>
/// Where a window was when it closed (its normal, un-maximized bounds) and whether it was maximized. Position is in
/// physical pixels (the OS's coordinates); size in device-independent units (the window's Width/Height). Width/Height
/// are null for windows that size themselves (only their position is kept).
/// </summary>
public sealed record WindowPlacement(int X, int Y, double? Width, double? Height, bool Maximized = false);

/// <summary>The remembered placement of every window, by name (AppSettings key "windowPlacement").</summary>
public sealed record WindowPlacements(Dictionary<string, WindowPlacement> Windows);

/// <summary>A monitor's working area (excluding the taskbar) in physical pixels, and its scale (1.5 = 150 %).</summary>
public readonly record struct ScreenArea(int X, int Y, int Width, int Height, double Scaling);

/// <summary>What to apply when opening a window. <see cref="X"/>/<see cref="Y"/> null = let the window centre itself.</summary>
public sealed record WindowRestore(int? X, int? Y, double? Width, double? Height, bool Maximized);

public static class WindowPlacementMath
{
    /// <summary>Pixels of the title bar that must be on a screen for the saved position to be used.</summary>
    public const int VisibleTitleBar = 40;

    /// <summary>
    /// Makes a saved placement safe for the screens connected now: sizes are clamped to at least the window's minimum
    /// and at most the working area of the screen it lands on; the position is kept only if the top of the window
    /// (a <see cref="VisibleTitleBar"/>-pixel strip) is on some screen, then nudged so the window doesn't hang off
    /// that screen's edges. Otherwise the window opens centred at the (clamped) saved size. No screens known: the
    /// saved values as they are.
    /// </summary>
    public static WindowRestore Fit(WindowPlacement saved, IReadOnlyList<ScreenArea> screens, double minWidth = 0, double minHeight = 0)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(screens);
        double? Clamp(double? value, double min, double max) =>
            value is double v && double.IsFinite(v) && v > 0 ? Math.Max(min, Math.Min(v, max)) : null;

        if (screens.Count == 0) return new WindowRestore(saved.X, saved.Y, saved.Width, saved.Height, saved.Maximized);

        // The screen holding the most of the window's top strip (its title bar); at least a 40×40-pixel patch of it must
        // be on a screen to grab it. None = the saved spot is off every screen.
        var stripWidth = Math.Max(VisibleTitleBar, (int)(saved.Width ?? 0));
        var home = screens
            .Select(s => (Screen: s, Overlap: Overlap(saved.X, saved.Y, stripWidth, VisibleTitleBar, s)))
            .Where(x => x.Overlap >= (long)VisibleTitleBar * VisibleTitleBar)
            .OrderByDescending(x => x.Overlap)
            .Select(x => (ScreenArea?)x.Screen)
            .FirstOrDefault();

        var screen = home ?? screens[0];
        var scale = screen.Scaling > 0 ? screen.Scaling : 1;
        var width = Clamp(saved.Width, minWidth, screen.Width / scale);
        var height = Clamp(saved.Height, minHeight, screen.Height / scale);
        if (home is null) return new WindowRestore(null, null, width, height, saved.Maximized);

        // Keep the whole window on that screen where it fits.
        var pixelWidth = (int)Math.Round((width ?? 0) * scale);
        var pixelHeight = (int)Math.Round((height ?? 0) * scale);
        var x = Math.Max(screen.X, Math.Min(saved.X, screen.X + screen.Width - pixelWidth));
        var y = Math.Max(screen.Y, Math.Min(saved.Y, screen.Y + screen.Height - pixelHeight));
        return new WindowRestore(x, y, width, height, saved.Maximized);
    }

    private static long Overlap(int x, int y, int w, int h, ScreenArea s)
    {
        long width = Math.Min(x + w, s.X + s.Width) - Math.Max(x, s.X);
        long height = Math.Min(y + h, s.Y + s.Height) - Math.Max(y, s.Y);
        return width > 0 && height > 0 ? width * height : 0;
    }
}
