using GuitarPractice.Domain;
using GuitarPractice.Domain.Practice;
using Microsoft.EntityFrameworkCore;

namespace GuitarPractice.Infrastructure.Tests;

public sealed class ProgressStoreTests : IDisposable
{
    private static readonly DateOnly Monday = new(2026, 10, 12);

    private readonly TestDatabase _db = new();
    private readonly ProgressStore _store;
    private readonly int _block;
    private readonly int _other;

    public ProgressStoreTests()
    {
        _store = new ProgressStore(_db, _db.Clock);
        var ids = _db.Query(db => db.Subcategories.OrderBy(s => s.Id).Select(s => s.Id).Take(2).ToList());
        (_block, _other) = (ids[0], ids[1]);
    }

    public void Dispose() => _db.Dispose();

    private static ExerciseLog Ex(string name, int? bpm, NoteValue? value = NoteValue.Sixteenths) => new(name, bpm, value);

    [Fact]
    public void Save_stores_the_exercises_in_order_with_rating_and_notes()
    {
        _store.Save(Monday, _block, [Ex("3nps scale", 120), Ex("String skipping", 96, NoteValue.Eighths)], 4, "  clean today  ");

        var entry = _store.Get(Monday, _block)!;
        Assert.Equal((4, "clean today"), (entry.Rating, entry.Notes));
        Assert.Equal(
            [("3nps scale", (int?)120, (NoteValue?)NoteValue.Sixteenths), ("String skipping", 96, NoteValue.Eighths)],
            entry.Exercises.Select(x => (x.Name, x.Bpm, x.NoteValue)));
        Assert.Equal(_db.Clock.Now, entry.UpdatedAt);
    }

    [Fact]
    public void Saving_again_replaces_the_exercise_list()
    {
        _store.Save(Monday, _block, [Ex("A", 100), Ex("B", 110), Ex("C", 120)], 3, null);
        _store.Save(Monday, _block, [Ex("B", 115)], 3, null);

        Assert.Equal(["B"], _store.Get(Monday, _block)!.Exercises.Select(x => x.Name));
        Assert.Equal(1, _db.Query(db => db.ProgressEntries.Count()));
        Assert.Equal(1, _db.Query(db => db.ProgressExercises.Count()));
    }

    [Fact]
    public void Lines_are_cleaned()
    {
        _store.Save(Monday, _block,
        [
            Ex("  " + new string('x', 150), 0),          // name capped, BPM 0 dropped
            Ex("   ", null),                               // nothing: removed
            Ex("", 140, (NoteValue)9),                     // unnamed BPM kept; bad note value dropped
        ], 9, "  " + new string('n', 2_500));

        var entry = _store.Get(Monday, _block)!;
        Assert.Equal(2, entry.Exercises.Count);
        Assert.Equal((ProgressExercise.MaxNameLength, (int?)null), (entry.Exercises[0].Name.Length, entry.Exercises[0].Bpm));
        Assert.Equal(("", (int?)140, (NoteValue?)null), (entry.Exercises[1].Name, entry.Exercises[1].Bpm, entry.Exercises[1].NoteValue));
        Assert.Null(entry.Rating);
        Assert.Equal(ProgressEntry.MaxNotesLength, entry.Notes!.Length);
    }

    [Fact]
    public void Saving_nothing_deletes_the_entry_and_its_exercises()
    {
        _store.Save(Monday, _block, [Ex("A", 100)], null, null);
        _store.Save(Monday, _block, [Ex(" ", null)], null, "   ");

        Assert.Null(_store.Get(Monday, _block));
        Assert.Equal((0, 0), (_db.Query(db => db.ProgressEntries.Count()), _db.Query(db => db.ProgressExercises.Count())));
        _store.Save(Monday, _other, [], null, null); // nothing to delete: no-op
    }

    [Fact]
    public void History_joins_practice_time_and_entries_by_day()
    {
        _db.Logs.Save(new PracticeKey(Monday, _block), new PracticeState(600, true));
        _db.Logs.Save(new PracticeKey(Monday.AddDays(1), _block), new PracticeState(300, false));
        _store.Save(Monday.AddDays(1), _block, [Ex("Lick 1", 110), Ex("Lick 2", 90, NoteValue.Triplets)], 4, "clean");
        _store.Save(Monday.AddDays(3), _block, [Ex("Lick 1", 115)], null, null);
        _db.Logs.Save(new PracticeKey(Monday, _other), new PracticeState(900, true));

        var history = _store.History(_block);

        Assert.Equal([Monday, Monday.AddDays(1), Monday.AddDays(3)], history.Select(d => d.Date));
        Assert.Equal((600, 0), (history[0].SecondsPractised, history[0].Exercises.Count));
        Assert.Equal([Ex("Lick 1", 110), Ex("Lick 2", 90, NoteValue.Triplets)], history[1].Exercises);
        Assert.Equal((4, "clean"), (history[1].Rating, history[1].Notes));
        Assert.Equal((0, 115), (history[2].SecondsPractised, history[2].Exercises[0].Bpm!.Value));
        Assert.Equal(new HashSet<int> { _block, _other }, _store.SubcategoriesWithHistory());
    }

    [Fact]
    public void Exercise_name_suggestions_are_per_block_most_recent_first_without_duplicates()
    {
        _store.Save(Monday, _block, [Ex("3nps scale", 100), Ex("String skipping", 90)], null, null);
        _store.Save(Monday.AddDays(2), _block, [Ex("3NPS scale", 110), Ex("Sweep arps", 80)], null, null);
        _store.Save(Monday, _other, [Ex("Other block thing", 70)], null, null);
        _store.Save(Monday.AddDays(1), _block, [Ex("", 50)], null, null); // unnamed: never suggested

        Assert.Equal(["3NPS scale", "Sweep arps", "String skipping"], _store.ExerciseNames(_block));
    }

    [Fact]
    public void Loading_a_saved_week_keeps_progress_history()
    {
        _db.Logs.Save(new PracticeKey(Monday, _block), new PracticeState(600, true));
        _store.Save(Monday, _block, [Ex("Lick", 120)], 5, "personal best");
        var (plan, request) = _db.MakePlan(Monday);
        _db.Weeks.Save("Block A", plan, request, routineId: null);

        _db.Weeks.Load(_db.Weeks.ListSaved().Single().Id, Monday); // clears that week's practice time, by design

        var day = Assert.Single(_store.History(_block));
        Assert.Equal((0, 120, "personal best"), (day.SecondsPractised, day.Exercises.Single().Bpm!.Value, day.Notes));
    }

    [Fact]
    public void Exercise_targets_can_be_set_changed_and_removed()
    {
        var changes = 0;
        _store.Changed += (_, id) => { if (id == _block) changes++; };

        _store.SetTarget(_block, "Rolling sweeps", NoteValue.Sixteenths, 140);
        _store.SetTarget(_block, " rolling  SWEEPS ", NoteValue.Sixteenths, 150);   // same exercise (normalised): updated
        _store.SetTarget(_block, "Rolling sweeps", NoteValue.Eighths, 200);        // other note value: separate target
        _store.SetTarget(_other, "Rolling sweeps", NoteValue.Sixteenths, 90);      // other block: separate

        var targets = _store.Targets(_block);
        Assert.Equal(2, targets.Count);
        Assert.Equal(150, targets[ProgressStats.ExerciseKey("Rolling sweeps", NoteValue.Sixteenths)]);
        Assert.Equal(200, targets[ProgressStats.ExerciseKey("Rolling sweeps", NoteValue.Eighths)]);

        _store.SetTarget(_block, "Rolling sweeps", NoteValue.Eighths, null);       // removed
        _store.SetTarget(_block, "Rolling sweeps", NoteValue.Sixteenths, 0);       // out of range: removed
        Assert.Empty(_store.Targets(_block));
        Assert.Single(_store.Targets(_other));
        Assert.Equal(5, changes);
    }

    [Fact]
    public void Block_target_can_be_set_and_cleared()
    {
        var changes = 0;
        _store.Changed += (_, id) => { if (id == _block) changes++; };
        _store.SetBlockTarget(_block, 175);
        Assert.Equal(175, _store.BlockTarget(_block));
        _store.SetBlockTarget(_block, null);
        Assert.Null(_store.BlockTarget(_block));
        _store.SetBlockTarget(_block, 999);
        Assert.Null(_store.BlockTarget(_block));
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Constraints_hold_in_the_database_too()
    {
        using var db = _db.CreateDbContext();
        var entry = new ProgressEntry { Date = Monday, SubcategoryId = _block, Rating = 3 };
        entry.Exercises.Add(new ProgressExercise { Name = "X", Bpm = 900 });
        db.ProgressEntries.Add(entry);
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public void Renaming_an_exercise_relabels_its_lines_and_moves_its_target()
    {
        _store.Save(Monday, _block, [Ex("Rolling sweps", 80), Ex("Trills", 150)], null, null);
        _store.Save(Monday.AddDays(1), _block, [Ex("rolling  SWEPS", 90), Ex("Rolling sweps", 70, NoteValue.Eighths)], null, null);
        _store.SetTarget(_block, "Rolling sweps", NoteValue.Sixteenths, 140);
        var changes = 0;
        _store.Changed += (_, id) => { if (id == _block) changes++; };

        Assert.Equal(2, _store.RenameExercise(_block, "Rolling sweps", NoteValue.Sixteenths, "  Rolling sweeps "));
        Assert.Equal(1, changes);
        var lines = _store.History(_block).SelectMany(d => d.Exercises).ToList();
        Assert.Equal(["Rolling sweeps", "Trills", "Rolling sweeps", "Rolling sweps"], lines.Select(l => l.Name));  // 8ths untouched
        Assert.Equal(140, _store.Targets(_block)[ProgressStats.ExerciseKey("Rolling sweeps", NoteValue.Sixteenths)]);
        Assert.False(_store.Targets(_block).ContainsKey(ProgressStats.ExerciseKey("Rolling sweps", NoteValue.Sixteenths)));

        Assert.Equal(0, _store.RenameExercise(_block, "Rolling sweeps", NoteValue.Sixteenths, "Rolling sweeps")); // nothing to do
        Assert.Throws<ArgumentException>(() => _store.RenameExercise(_block, "Trills", NoteValue.Sixteenths, "   "));
    }

    [Fact]
    public void Renaming_to_an_existing_exercise_merges_them_and_keeps_its_target()
    {
        _store.Save(Monday, _block, [Ex("Sweeps", 80), Ex("Arpeggio sweeps", 100)], null, null);
        _store.Save(Monday.AddDays(1), _block, [Ex("Sweeps", 90)], null, null);
        _store.SetTarget(_block, "Sweeps", NoteValue.Sixteenths, 120);
        _store.SetTarget(_block, "Arpeggio sweeps", NoteValue.Sixteenths, 150);

        Assert.Equal(2, _store.RenameExercise(_block, "Sweeps", NoteValue.Sixteenths, "arpeggio  SWEEPS"));
        Assert.All(_store.History(_block).SelectMany(d => d.Exercises), x => Assert.Equal("Arpeggio sweeps", x.Name)); // its spelling

        var s = ProgressStats.Summarize(_store.History(_block), null, null, Monday.AddDays(1), _store.Targets(_block));
        var merged = Assert.Single(s.Exercises);
        Assert.Equal((100, 3, 150), (merged.BestBpm!.Value, merged.TimesLogged, merged.TargetBpm!.Value)); // 3 lines; its target kept
        Assert.Single(_store.Targets(_block));
    }

    [Fact]
    public void Removing_an_exercise_keeps_its_history_and_the_same_name_starts_over()
    {
        _store.Save(Monday, _block, [Ex("Rolling sweeps", 80), Ex("Trills", 150)], 4, "day one");
        _store.Save(Monday.AddDays(1), _block, [Ex("rolling  SWEEPS", 90)], null, null);
        _store.SetTarget(_block, "Rolling sweeps", NoteValue.Sixteenths, 140);
        var changes = 0;
        _store.Changed += (_, id) => { if (id == _block) changes++; };

        Assert.Equal(2, _store.RemoveExercise(_block, " Rolling Sweeps ", NoteValue.Sixteenths));
        Assert.Equal(0, _store.RemoveExercise(_block, "Rolling sweeps", NoteValue.Sixteenths)); // nothing current left
        Assert.Equal(0, _store.RemoveExercise(_block, "Trills", NoteValue.Eighths));            // other note value: not this one
        Assert.Equal(1, changes);

        var history = _store.History(_block);
        Assert.Equal([Ex("Rolling sweeps", 80) with { RemovedRun = 1 }, Ex("Trills", 150)], history[0].Exercises); // nothing deleted
        Assert.Equal((4, "day one"), (history[0].Rating, history[0].Notes));

        // Logging the name again starts a new run; removing that one makes run 2.
        _store.Save(Monday.AddDays(3), _block, [Ex("Rolling sweeps", 60)], null, null);
        var s = ProgressStats.Summarize(_store.History(_block), null, null, Monday.AddDays(3), _store.Targets(_block));
        var fresh = s.Exercises.Single(e => e.Name == "Rolling sweeps");
        Assert.Equal((60, 1, 140), (fresh.BestBpm!.Value, fresh.TimesLogged, fresh.TargetBpm!.Value)); // own target kept
        Assert.Equal((90, 1), (s.Removed.Single().BestBpm!.Value, s.Removed.Single().RemovedRun!.Value));

        _store.RemoveExercise(_block, "Rolling sweeps", NoteValue.Sixteenths);
        Assert.Equal([1, 2], _store.History(_block).SelectMany(d => d.Exercises).Where(x => x.Name.StartsWith("R", StringComparison.OrdinalIgnoreCase)).Select(x => x.RemovedRun!.Value).Distinct().Order());
    }

    [Fact]
    public void Saving_a_day_keeps_which_lines_were_removed()
    {
        _store.Save(Monday, _block, [Ex("Old", 80), Ex("Keep", 100)], null, null);
        _store.RemoveExercise(_block, "Old", NoteValue.Sixteenths);

        // An editor loads the day (with the marker), changes the rating, saves.
        var lines = _store.History(_block)[0].Exercises;
        _store.Save(Monday, _block, lines, 5, null);

        Assert.Equal([1, null], _store.Get(Monday, _block)!.Exercises.Select(x => x.RemovedRun));
        _store.Save(Monday, _block, [Ex("Bad run", 1) with { RemovedRun = 0 }], null, null); // out of range: current
        Assert.Null(Assert.Single(_store.Get(Monday, _block)!.Exercises).RemovedRun);
    }

    [Fact]
    public void Quintuplets_to_32nds_are_stored_and_unknown_note_values_dropped()
    {
        _store.Save(Monday, _block, [Ex("Fives", 72, NoteValue.Quintuplets), Ex("Sixes", 66, NoteValue.Sextuplets),
            Ex("Sevens", 60, NoteValue.Septuplets), Ex("Bursts", 80, NoteValue.ThirtySeconds), Ex("Bad", 50, (NoteValue)9)], null, null);
        Assert.Equal([NoteValue.Quintuplets, NoteValue.Sextuplets, NoteValue.Septuplets, NoteValue.ThirtySeconds, null],
            _store.Get(Monday, _block)!.Exercises.Select(x => x.NoteValue));
    }
}
