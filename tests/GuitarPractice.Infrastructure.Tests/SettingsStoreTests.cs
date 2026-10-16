namespace GuitarPractice.Infrastructure.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private sealed record Prefs(double Bpm, int Beats, string? Name = null);

    private readonly TestDatabase _db = new();
    private readonly SettingsStore _store;

    public SettingsStoreTests() => _store = new SettingsStore(_db, _db.Clock);

    public void Dispose() => _db.Dispose();

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
