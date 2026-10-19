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
    public void Per_weekday_minutes_persist_including_rest_days()
    {
        var routine = _db.Library.GetOrCreateDefaultRoutine();
        var week = new Dictionary<DayOfWeek, int>
        {
            [DayOfWeek.Monday] = 30, [DayOfWeek.Tuesday] = 45, [DayOfWeek.Wednesday] = 0, [DayOfWeek.Thursday] = 60,
            [DayOfWeek.Friday] = 20, [DayOfWeek.Saturday] = 120, [DayOfWeek.Sunday] = -15,
        };

        _db.Library.SaveSettings(routine.Id, week, pinnedTimeOnTop: false);

        var stored = _db.Library.GetOrCreateDefaultRoutine().DayBudgets.ToDictionary(b => b.Day, b => b.Minutes);
        Assert.Equal(7, stored.Count);
        Assert.Equal(0, stored[DayOfWeek.Wednesday]);   // rest day
        Assert.Equal(0, stored[DayOfWeek.Sunday]);      // negative stored as 0
        Assert.Equal(120, stored[DayOfWeek.Saturday]);
        Assert.Equal(45, stored[DayOfWeek.Tuesday]);
    }

    [Fact]
    public void Days_not_given_keep_their_minutes()
    {
        var routine = _db.Library.GetOrCreateDefaultRoutine();
        _db.Library.SaveSettings(routine.Id, 50, pinnedTimeOnTop: false);
        _db.Library.SaveSettings(routine.Id, new Dictionary<DayOfWeek, int> { [DayOfWeek.Friday] = 10 }, pinnedTimeOnTop: true);

        var reloaded = _db.Library.GetOrCreateDefaultRoutine();
        Assert.True(reloaded.PinnedTimeOnTopOfBudget);
        Assert.All(reloaded.DayBudgets, b => Assert.Equal(b.Day == DayOfWeek.Friday ? 10 : 50, b.Minutes));
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
    public void Removed_subcategories_are_listed_and_can_be_restored_without_rejoining_the_routine()
    {
        var routine = _db.Library.GetOrCreateDefaultRoutine();
        var removed = routine.Items.First(i => i.Weight == 5).SubcategoryId;
        Assert.Empty(_db.Library.LoadRemoved());

        _db.Library.Archive(removed);
        var listed = Assert.Single(_db.Library.LoadRemoved());
        Assert.Equal(removed, listed.Id);
        Assert.NotNull(listed.Category);

        var restored = _db.Library.Restore(removed);
        Assert.False(restored.IsArchived);
        Assert.Empty(_db.Library.LoadRemoved());
        Assert.Contains(_db.Library.LoadLibrary().SelectMany(c => c.Subcategories), s => s.Id == removed);
        Assert.DoesNotContain(_db.Library.GetOrCreateDefaultRoutine().Items, i => i.SubcategoryId == removed); // weight 0
    }

    [Fact]
    public void A_removed_category_leaves_library_and_routine_and_comes_back_with_its_items()
    {
        var routine = _db.Library.GetOrCreateDefaultRoutine();
        var inRoutine = routine.Items.First(i => i.Weight == 5).SubcategoryId;
        var category = _db.Library.LoadLibrary().First(c => c.Subcategories.Any(s => s.Id == inRoutine));
        var alreadyRemoved = category.Subcategories.First(s => s.Id != inRoutine).Id;
        _db.Library.Archive(alreadyRemoved);                      // removed on its own before the category
        var items = category.Subcategories.Select(s => s.Id).Where(id => id != alreadyRemoved).ToList();

        _db.Library.ArchiveCategory(category.Id);

        Assert.DoesNotContain(_db.Library.LoadLibrary(), c => c.Id == category.Id);
        Assert.DoesNotContain(_db.Library.GetOrCreateDefaultRoutine().Items, i => items.Contains(i.SubcategoryId));
        var listed = Assert.Single(_db.Library.LoadRemovedCategories());
        Assert.Equal(items, listed.Subcategories.Select(s => s.Id));
        Assert.Empty(_db.Library.LoadRemoved());                  // its items aren't listed one by one
        Assert.Equal(items.Append(alreadyRemoved).Order(), _db.Library.LoadAllRemoved().Select(s => s.Id).Order()); // Progress keeps all

        var restored = _db.Library.RestoreCategory(category.Id);
        Assert.Equal(items, restored.Subcategories.Select(s => s.Id));
        Assert.Contains(_db.Library.LoadLibrary(), c => c.Id == category.Id);
        Assert.Empty(_db.Library.LoadRemovedCategories());
        Assert.Equal(alreadyRemoved, Assert.Single(_db.Library.LoadRemoved()).Id); // still removed on its own
        Assert.DoesNotContain(_db.Library.GetOrCreateDefaultRoutine().Items, i => items.Contains(i.SubcategoryId)); // weight 0
    }

    [Fact]
    public void Removing_a_built_in_subcategory_keeps_its_practice_and_progress_history()
    {
        var builtIn = _db.Query(db => db.Subcategories.First(s => s.IsBuiltIn).Id);
        var day = new DateOnly(2026, 10, 12);
        _db.Logs.Save(new PracticeKey(day, builtIn), new PracticeState(600, true));
        var progress = new ProgressStore(_db, _db.Clock);
        progress.Save(day, builtIn, [new GuitarPractice.Domain.Practice.ExerciseLog("Drill", 120, NoteValue.Sixteenths)], 4, null);

        _db.Library.Archive(builtIn);

        Assert.DoesNotContain(_db.Library.LoadLibrary().SelectMany(c => c.Subcategories), s => s.Id == builtIn);
        var history = Assert.Single(progress.History(builtIn));
        Assert.Equal((600, 120), (history.SecondsPractised, history.Exercises.Single().Bpm!.Value));
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
