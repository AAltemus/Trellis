using GuitarPractice.Domain;
using GuitarPractice.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuitarPractice.Infrastructure.Tests;

public sealed class LibraryStoreTests : IDisposable
{
    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Seed_library_loads_in_order()
    {
        var library = _db.Library.LoadLibrary();

        Assert.Equal(12, library.Count);
        Assert.Equal(78, library.Sum(c => c.Subcategories.Count));
        Assert.Equal(library.OrderBy(c => c.SortOrder).ThenBy(c => c.Id).Select(c => c.Id), library.Select(c => c.Id));
        Assert.Single(library, c => c.Kind == CategoryKind.Songs);
        Assert.Equal(6, _db.Library.LoadTunings().Count);
    }

    [Fact]
    public void Default_routine_is_created_once_with_one_starter_per_exercise_category()
    {
        var first = _db.Library.GetOrCreateDefaultRoutine();
        var second = _db.Library.GetOrCreateDefaultRoutine();

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, _db.Query(db => db.Routines.Count()));

        // Expected: the first built-in subcategory (by SortOrder, Id) of every exercise category.
        var subcategories = _db.Query(db => db.Subcategories.Include(s => s.Category).AsNoTracking().ToList());
        var expected = subcategories
            .Where(s => s.IsBuiltIn && !s.IsArchived && s.Category.Kind == CategoryKind.Exercise)
            .GroupBy(s => s.CategoryId)
            .Select(g => g.OrderBy(s => s.SortOrder).ThenBy(s => s.Id).First())
            .ToDictionary(s => s.Id, s => s.Pin == PinMode.None ? 5 : 1);

        Assert.Equal(expected.OrderBy(kv => kv.Key), second.Items.ToDictionary(i => i.SubcategoryId, i => i.Weight).OrderBy(kv => kv.Key));
        Assert.Contains(second.Items, i => i.Weight == 1); // the warmup is pinned and stored at weight 1
        Assert.Equal(7, second.DayBudgets.Count);
        Assert.All(second.DayBudgets, b => Assert.Equal(60, b.Minutes));
    }

    [Fact]
    public void SetWeight_inserts_updates_clamps_and_removes()
    {
        var routine = _db.Library.GetOrCreateDefaultRoutine();
        var subcategoryId = _db.Query(db => db.Subcategories
            .Where(s => s.Pin == PinMode.None)
            .Select(s => s.Id)
            .AsEnumerable()
            .First(id => routine.Items.All(i => i.SubcategoryId != id)));

        int? Weight() => _db.Query(db => db.RoutineItems.AsNoTracking()
            .Where(i => i.RoutineId == routine.Id && i.SubcategoryId == subcategoryId)
            .Select(i => (int?)i.Weight)
            .SingleOrDefault());

        _db.Library.SetWeight(routine.Id, subcategoryId, 3);
        Assert.Equal(3, Weight());

        _db.Library.SetWeight(routine.Id, subcategoryId, 7);
        Assert.Equal(7, Weight());

        _db.Library.SetWeight(routine.Id, subcategoryId, 15);
        Assert.Equal(10, Weight());

        _db.Library.SetWeight(routine.Id, subcategoryId, 0);
        Assert.Null(Weight());

        _db.Library.SetWeight(routine.Id, subcategoryId, 0); // removing again is a no-op
        Assert.Null(Weight());
    }

    [Fact]
    public void SaveSettings_and_SaveSeed_persist()
    {
        var routine = _db.Library.GetOrCreateDefaultRoutine();

        _db.Library.SaveSettings(routine.Id, 45, pinnedTimeOnTop: true);
        _db.Library.SaveSeed(routine.Id, 1234);

        var reloaded = _db.Library.GetOrCreateDefaultRoutine();
        Assert.True(reloaded.PinnedTimeOnTopOfBudget);
        Assert.Equal(1234, reloaded.PlanSeed);
        Assert.Equal(7, reloaded.DayBudgets.Count);
        Assert.All(reloaded.DayBudgets, b => Assert.Equal(45, b.Minutes));
    }

    [Fact]
    public void AddSubcategory_stores_song_details()
    {
        var songs = _db.Library.LoadLibrary().Single(c => c.Kind == CategoryKind.Songs);
        var tuning = _db.Library.LoadTunings()[1];

        var song = _db.Library.AddSubcategory(songs.Id, "  Tornado of Souls ",
            new SongDetails("Megadeth", tuning.Id, SongStatus.Polishing, "E minor", "https://example.com", 200));
        var drill = _db.Library.AddSubcategory(songs.Id, "Second");

        var stored = _db.Query(db => db.Subcategories.AsNoTracking().Single(s => s.Id == song.Id));
        Assert.Equal("Tornado of Souls", stored.Name);
        Assert.Equal("Megadeth", stored.Artist);
        Assert.Equal(tuning.Id, stored.TuningId);
        Assert.Equal(SongStatus.Polishing, stored.Status);
        Assert.Equal("E minor", stored.SongKey);
        Assert.Equal("https://example.com", stored.ReferenceUrl);
        Assert.Equal(200, stored.TargetBpm);
        Assert.Equal((15, 30), (stored.MinSessionMinutes, stored.MaxSessionMinutes));
        Assert.False(stored.IsBuiltIn);

        Assert.Equal((10, 20), (drill.MinSessionMinutes, drill.MaxSessionMinutes));
        Assert.Equal(stored.SortOrder + 1, drill.SortOrder);
    }

    [Fact]
    public void Archive_hides_the_subcategory_and_removes_it_from_routines()
    {
        var routine = _db.Library.GetOrCreateDefaultRoutine();
        var archived = routine.Items.First(i => i.Weight == 5).SubcategoryId;

        _db.Library.Archive(archived);

        Assert.DoesNotContain(_db.Library.LoadLibrary().SelectMany(c => c.Subcategories), s => s.Id == archived);
        Assert.DoesNotContain(_db.Library.GetOrCreateDefaultRoutine().Items, i => i.SubcategoryId == archived);
        Assert.True(_db.Query(db => db.Subcategories.Single(s => s.Id == archived).IsArchived)); // soft delete
    }

    [Fact]
    public void User_rows_get_ids_above_the_built_in_range_after_reservation()
    {
        using (var db = _db.CreateDbContext())
        {
            DatabaseInitializer.ReserveBuiltInIds(db);
            DatabaseInitializer.ReserveBuiltInIds(db); // idempotent
        }

        var category = _db.Library.AddCategory("Tapping");
        var subcategory = _db.Library.AddSubcategory(category.Id, "Eight-finger runs");

        Assert.Equal(10001, category.Id);
        Assert.Equal(10001, subcategory.Id);
        Assert.Contains(_db.Library.LoadLibrary(), c => c.Id == category.Id && c.Subcategories.Any(s => s.Id == subcategory.Id));
    }
}
