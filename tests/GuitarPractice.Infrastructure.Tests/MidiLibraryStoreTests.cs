namespace GuitarPractice.Infrastructure.Tests;

public sealed class MidiLibraryStoreTests : IDisposable
{
    private static readonly int[] OldMuted = [1];
    private readonly TestDatabase _db = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "trellis-midi-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MidiLibraryStore _store;

    public MidiLibraryStoreTests()
    {
        Directory.CreateDirectory(_root);
        _store = new MidiLibraryStore(new SettingsStore(_db, _db.Clock), Path.Combine(_root, "midi"));
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string Source(string name, int bytes = 64)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void Import_copies_into_the_library_and_keeps_names_unique()
    {
        Assert.Empty(_store.List()); // no folder yet

        var first = _store.Import(Source("Master of Puppets.mid", 100));
        var second = _store.Import(Source("Master of Puppets.mid"));
        var third = _store.Import(Source("Master of Puppets.MIDI"));

        Assert.Equal(["Master of Puppets (2).mid", "Master of Puppets (3).midi", "Master of Puppets.mid"], _store.List().Select(i => i.FileName)); // unique by shown name
        Assert.Equal(("Master of Puppets", 100L), (first.DisplayName, first.Bytes));
        Assert.Equal("Master of Puppets (2)", second.DisplayName);
        Assert.True(File.Exists(Path.Combine(_root, "Master of Puppets.mid"))); // the original stays
        Assert.StartsWith(_store.Directory, third.Path, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => _store.Import(Source("notes.txt")));
    }

    [Fact]
    public void Settings_and_links_follow_a_rename_and_go_with_a_delete()
    {
        var item = _store.Import(Source("riff.mid"));
        var other = _store.Import(Source("other.mid"));
        var settings = new MidiFileSettings(SpeedPercent: 70, LoopEnabled: true, LoopFromBar: 9, LoopToBar: 12, MutedTracks: [2]);
        _store.SaveSettings(item.FileName, settings);
        _store.Link(501, item.FileName);
        _store.Link(502, item.FileName);
        _store.Link(503, other.FileName);

        var renamed = _store.Rename(item.FileName, "  Riff: verse?  ");
        Assert.Equal("Riff verse.mid", renamed.FileName);              // invalid characters dropped, extension kept
        Assert.Equal(70, _store.GetSettings("riff verse.mid").SpeedPercent); // case-insensitive
        Assert.Equal([2], _store.GetSettings(renamed.FileName).MutedTracks);
        Assert.Equal(renamed.FileName, _store.LinkedFile(501)!.FileName);
        Assert.Equal([501, 502], _store.LinksTo(renamed.FileName));

        Assert.Throws<IOException>(() => _store.Rename(renamed.FileName, "other"));
        Assert.Throws<ArgumentException>(() => _store.Rename(renamed.FileName, " ?* "));

        _store.Delete(renamed.FileName);
        Assert.Equal(["other.mid"], _store.List().Select(i => i.FileName));
        Assert.Null(_store.LinkedFile(501));
        Assert.Equal(new MidiFileSettings(), _store.GetSettings(renamed.FileName));
        Assert.Equal("other.mid", _store.LinkedFile(503)!.FileName);  // untouched
    }

    [Fact]
    public void Track_volumes_and_count_in_each_loop_are_kept_and_older_rows_load_with_defaults()
    {
        var item = _store.Import(Source("song.mid"));
        _store.SaveSettings(item.FileName, new MidiFileSettings(CountInEachLoop: true, TrackVolumes: new() { [3] = 40, [5] = 0 }));
        var back = _store.GetSettings(item.FileName);
        Assert.True(back.CountInEachLoop);
        Assert.Equal(new Dictionary<int, int> { [3] = 40, [5] = 0 }, back.TrackVolumes);

        // A row saved before these fields existed.
        var settings = new SettingsStore(_db, _db.Clock);
        settings.Set(MidiLibraryStore.SettingsKey, new
        {
            Files = new Dictionary<string, object> { ["song.mid"] = new { SpeedPercent = 80, CountIn = true, MutedTracks = OldMuted } },
            Links = new Dictionary<int, string>(),
        });
        var old = _store.GetSettings(item.FileName);
        Assert.Equal((80, true, false, null), (old.SpeedPercent, old.CountIn, old.CountInEachLoop, old.TrackVolumes));
        Assert.Equal([1], old.MutedTracks);
    }

    [Fact]
    public void Links_can_be_replaced_and_removed_and_a_missing_file_isnt_returned()
    {
        var a = _store.Import(Source("a.mid"));
        var b = _store.Import(Source("b.mid"));
        _store.Link(7, a.FileName);
        _store.Link(7, b.FileName);
        Assert.Equal("b.mid", _store.LinkedFile(7)!.FileName);

        File.Delete(b.Path); // deleted outside the app
        Assert.Null(_store.LinkedFile(7));

        _store.Unlink(7);
        _store.Unlink(7); // no-op
        Assert.Empty(_store.LinksTo("b.mid"));
        Assert.Equal(new MidiFileSettings(), _store.GetSettings("never-seen.mid"));
    }

    [Fact]
    public void Clean_names()
    {
        Assert.Equal("acdc", MidiLibraryStore.CleanName(" ac/dc "));
        Assert.Equal("Riff verse", MidiLibraryStore.CleanName("Riff: verse?"));               // Windows' rules on every OS
        Assert.Equal("abcdefgh", MidiLibraryStore.CleanName("a\\b:c*d?e\"f<g>h|\t"));
        Assert.All("\\/:*?\"<>|\0\n", c => Assert.True(FileNames.IsInvalid(c)));
        Assert.All("aZ09 -_.,()'&!é", c => Assert.False(FileNames.IsInvalid(c)));
        Assert.Equal("song", MidiLibraryStore.CleanName("song..."));
        Assert.Equal("", MidiLibraryStore.CleanName(null));
        Assert.Equal(120, MidiLibraryStore.CleanName(new string('x', 300)).Length);
    }
}
