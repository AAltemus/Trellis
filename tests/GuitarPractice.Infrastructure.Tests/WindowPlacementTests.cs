namespace GuitarPractice.Infrastructure.Tests;

public sealed class WindowPlacementTests
{
    // 1920×1080 at 100 % with a 40 px taskbar, and a 2560×1440 monitor at 150 % to its right.
    private static readonly ScreenArea Left = new(0, 0, 1920, 1040, 1.0);
    private static readonly ScreenArea Right = new(1920, 0, 2560, 1400, 1.5);

    [Fact]
    public void A_saved_placement_that_fits_comes_back_unchanged()
    {
        var r = WindowPlacementMath.Fit(new WindowPlacement(100, 80, 1300, 800), [Left, Right], 1024, 640);
        Assert.Equal(new WindowRestore(100, 80, 1300, 800, false), r);

        var maximized = WindowPlacementMath.Fit(new WindowPlacement(2000, 50, 1200, 700, Maximized: true), [Left, Right]);
        Assert.Equal(new WindowRestore(2000, 50, 1200, 700, true), maximized);
    }

    [Fact]
    public void Off_every_screen_opens_centred_at_the_saved_size()
    {
        // A monitor that is gone: far to the right.
        var r = WindowPlacementMath.Fit(new WindowPlacement(5000, 100, 1300, 800), [Left], 1024, 640);
        Assert.Equal(new WindowRestore(null, null, 1300, 800, false), r);

        // Above the top of the screen: the title bar can't be grabbed.
        Assert.Null(WindowPlacementMath.Fit(new WindowPlacement(100, -500, 800, 600), [Left]).X);
    }

    [Fact]
    public void Too_big_for_the_screen_is_shrunk_but_never_below_the_minimum()
    {
        var r = WindowPlacementMath.Fit(new WindowPlacement(0, 0, 2400, 1300), [Left], 1024, 640);
        Assert.Equal((1920.0, 1040.0), (r.Width!.Value, r.Height!.Value));

        // On the 150 % screen its working area is 2560/1.5 × 1400/1.5 device-independent units.
        var scaled = WindowPlacementMath.Fit(new WindowPlacement(1920, 0, 3000, 2000), [Left, Right]);
        Assert.Equal(2560 / 1.5, scaled.Width!.Value, 6);
        Assert.Equal(1400 / 1.5, scaled.Height!.Value, 6);

        var tiny = WindowPlacementMath.Fit(new WindowPlacement(10, 10, 300, 200), [Left], 1024, 640);
        Assert.Equal((1024.0, 640.0), (tiny.Width!.Value, tiny.Height!.Value));
    }

    [Fact]
    public void A_window_hanging_off_an_edge_is_pulled_back_onto_its_screen()
    {
        // Mostly on the left screen, its right part past the bottom-right corner.
        var r = WindowPlacementMath.Fit(new WindowPlacement(1500, 700, 800, 600), [Left]);
        Assert.Equal((1920 - 800, 1040 - 600), (r.X!.Value, r.Y!.Value));

        var negative = WindowPlacementMath.Fit(new WindowPlacement(-100, 200, 800, 600), [Left]);
        Assert.Equal(0, negative.X);
    }

    [Fact]
    public void Self_sizing_windows_keep_only_their_position()
    {
        var r = WindowPlacementMath.Fit(new WindowPlacement(300, 200, null, null), [Left]);
        Assert.Equal(new WindowRestore(300, 200, null, null, false), r);
    }

    [Fact]
    public void Nonsense_sizes_are_ignored_and_no_known_screens_keeps_what_was_saved()
    {
        var r = WindowPlacementMath.Fit(new WindowPlacement(10, 10, double.NaN, -5), [Left]);
        Assert.Equal((null, null), (r.Width, r.Height));
        Assert.Equal(new WindowRestore(5000, 5000, 900, 700, false), WindowPlacementMath.Fit(new WindowPlacement(5000, 5000, 900, 700), []));
    }

    [Fact]
    public void Placements_round_trip_through_the_settings_store()
    {
        using var db = new TestDatabase();
        var store = new SettingsStore(db, db.Clock);
        var saved = new WindowPlacements(new() { ["main"] = new(10, 20, 1300, 800, true), ["metronome"] = new(400, 300, null, null) });

        store.Set("windowPlacement", saved);
        var loaded = store.Get<WindowPlacements>("windowPlacement")!;

        Assert.Equal(saved.Windows["main"], loaded.Windows["main"]);
        Assert.Equal(saved.Windows["metronome"], loaded.Windows["metronome"]);
    }
}
