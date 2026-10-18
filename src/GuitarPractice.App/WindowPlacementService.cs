using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using GuitarPractice.Infrastructure;

namespace GuitarPractice.App;

/// <summary>
/// Remembers where each window was and how big (AppSettings key "windowPlacement") and reopens it the same way. Call
/// <see cref="Attach"/> before the window is shown. Resizable windows keep size, position and maximized; windows that
/// size themselves (CanResize false) keep only their position. Saved values are fitted to the screens connected now
/// (<see cref="WindowPlacementMath.Fit"/>), so an unplugged monitor never leaves a window off screen.
/// </summary>
public sealed class WindowPlacementService(SettingsStore settings)
{
    public const string SettingsKey = "windowPlacement";

    public void Attach(Window window, string name)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var resizable = window.CanResize;
        if (Load().Windows.TryGetValue(name, out var saved))
            Apply(window, saved, resizable);

        // The bounds to save are the last *normal* ones: closing while maximized (or minimized) must not save the
        // maximized size as the normal size.
        var tracked = new Tracked(window.Position, window.Width, window.Height); // until it has been shown
        void Track()
        {
            if (window.WindowState != WindowState.Normal || !window.IsVisible) return;
            tracked = new Tracked(window.Position, window.ClientSize.Width, window.ClientSize.Height);
        }
        window.PositionChanged += (_, _) => Track();
        window.Resized += (_, _) => Track();
        window.Opened += (_, _) => Track();
        window.Closing += (_, _) =>
        {
            Track();
            Save(name, new WindowPlacement(
                tracked.Position.X,
                tracked.Position.Y,
                resizable && double.IsFinite(tracked.Width) ? tracked.Width : null,
                resizable && double.IsFinite(tracked.Height) ? tracked.Height : null,
                resizable && window.WindowState == WindowState.Maximized));
        };
    }

    private readonly record struct Tracked(PixelPoint Position, double Width, double Height);

    private static void Apply(Window window, WindowPlacement saved, bool resizable)
    {
        var screens = window.Screens.All
            .Select(s => new ScreenArea(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height, s.Scaling))
            .ToList();
        var fit = WindowPlacementMath.Fit(resizable ? saved : saved with { Width = null, Height = null }, screens,
            window.MinWidth, window.MinHeight);

        if (resizable && fit.Width is double w && fit.Height is double h)
        {
            window.Width = w;
            window.Height = h;
        }
        if (fit.X is int x && fit.Y is int y)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(x, y);
        }
        if (fit.Maximized && resizable)
            window.WindowState = WindowState.Maximized;
    }

    private WindowPlacements Load()
    {
        try
        {
            return settings.Get<WindowPlacements>(SettingsKey) ?? new WindowPlacements([]);
        }
#pragma warning disable CA1031 // A broken preference must never stop a window from opening.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Loading window placement failed: {ex.Message}");
            return new WindowPlacements([]);
        }
#pragma warning restore CA1031
    }

    private void Save(string name, WindowPlacement placement)
    {
        try
        {
            var all = Load();
            all.Windows[name] = placement;
            settings.Set(SettingsKey, all);
        }
#pragma warning disable CA1031 // Losing a window position must never break closing the app.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Saving window placement failed: {ex.Message}");
        }
#pragma warning restore CA1031
    }
}
