namespace GuitarPractice.Infrastructure.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private sealed record Prefs(double Bpm, int Beats, string? Name = null);

    private readonly TestDatabase _db = new();
    private readonly SettingsStore _store;

    public SettingsStoreTests() => _store = new SettingsStore(_db, _db.Clock);

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Progress_tracking_excludes_categories_and_blocks_and_takes_them_back()
    {
        var tracking = new ProgressTracking(_store);
        var changes = 0;
        tracking.Changed += (_, _) => changes++;
        Assert.True(tracking.IsTracked(1, 101));                     // nothing saved: everything tracked

        tracking.SetCategory(1, tracked: false);                       // e.g. Warmup
        tracking.SetBlock(302, tracked: false);
        tracking.SetBlock(302, tracked: false);                        // twice: still one entry
        Assert.False(tracking.IsTracked(1, 101));                     // its category is out
        Assert.False(tracking.IsTracked(3, 302));
        Assert.True(tracking.IsTracked(3, 301));
        Assert.Equal([302], tracking.Load().ExcludedBlocks);

        tracking.SetCategory(1, tracked: true);
        Assert.True(new ProgressTracking(new SettingsStore(_db, _db.Clock)).IsTracked(1, 101)); // saved
        Assert.Equal(4, changes);
    }

    [Fact]
    public void Take_links_follow_renames_and_deletes_and_list_by_block_and_day()
    {
        var links = new TakeLinkStore(_store);
        var day = new DateOnly(2026, 10, 18);
        var changes = 0;
        links.Changed += (_, _) => changes++;

        links.Set(@"C:\data\recordings\Take 1.wav", new TakeLink(301, day, "  3nps scale "));
        links.Set("Take 2.wav", new TakeLink(301, day));
        links.Set("Take 3.wav", new TakeLink(301, day.AddDays(-1), " "));
        links.Set("Other.wav", new TakeLink(402, day));

        Assert.Equal(new TakeLink(301, day, "3nps scale"), links.Get("take 1.WAV"));  // by file name, any case
        Assert.Null(links.Get("Take 3.wav")!.Exercise);
        Assert.Equal(["Take 1.wav", "Take 2.wav"], links.For(301, day));
        Assert.Equal(new Dictionary<DateOnly, int> { [day] = 2, [day.AddDays(-1)] = 1 }, links.CountsByDay(301));

        links.Rename("Take 1.wav", "Sweeps clean.wav");
        links.Remove("Take 2.wav");
        links.Remove("Never linked.wav");                                       // no-op, no event
        Assert.Equal(["Sweeps clean.wav"], links.For(301, day));
        Assert.Equal("3nps scale", new TakeLinkStore(new SettingsStore(_db, _db.Clock)).Get("Sweeps clean.wav")?.Exercise); // saved
        Assert.Equal(6, changes);
    }

    [Fact]
    public void Missing_key_reads_as_null()
    {
        Assert.Null(_store.Get<Prefs>("metronome"));
    }

    [Fact]
    public void Set_then_get_round_trips_and_overwrites()
    {
        _store.Set("metronome", new Prefs(132, 7, "odd"));
        Assert.Equal(new Prefs(132, 7, "odd"), _store.Get<Prefs>("metronome"));

        _db.Clock.Now = _db.Clock.Now.AddHours(1);
        _store.Set("metronome", new Prefs(90, 3));
        Assert.Equal(new Prefs(90, 3), _store.Get<Prefs>("metronome"));

        var row = Assert.Single(_db.Query(db => db.AppSettings.ToList()));
        Assert.Equal(_db.Clock.Now, row.UpdatedAt);
    }

    [Fact]
    public void Keys_are_independent()
    {
        _store.Set("a", new Prefs(1, 1));
        _store.Set("b", new Prefs(2, 2));
        Assert.Equal(1, _store.Get<Prefs>("a")!.Bpm);
        Assert.Equal(2, _store.Get<Prefs>("b")!.Bpm);
    }

    [Fact]
    public void Unreadable_json_reads_as_null()
    {
        using (var db = _db.CreateDbContext())
        {
            db.AppSettings.Add(new Domain.AppSetting { Key = "metronome", Json = "{not json" });
            db.SaveChanges();
        }
        Assert.Null(_store.Get<Prefs>("metronome"));
    }
}
