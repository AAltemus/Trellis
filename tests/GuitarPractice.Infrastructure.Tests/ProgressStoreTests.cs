using Microsoft.EntityFrameworkCore;

namespace GuitarPractice.Infrastructure.Tests;

public sealed class ProgressStoreTests : IDisposable
{
    private static readonly DateOnly Monday = new(2026, 10, 12);

    private readonly TestDatabase _db = new();
    private readonly ProgressStore _store;
    private readonly int _exercise;
    private readonly int _other;

    public ProgressStoreTests()
    {
        _store = new ProgressStore(_db, _db.Clock);
        var ids = _db.Query(db => db.Subcategories.OrderBy(s => s.Id).Select(s => s.Id).Take(2).ToList());
        (_exercise, _other) = (ids[0], ids[1]);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Save_upserts_one_entry_per_day_and_exercise()
    {
        _store.Save(Monday, _exercise, 90, 3, "  first go  ");
        _store.Save(Monday, _exercise, 96, 4, "better");

        var entry = _store.Get(Monday, _exercise)!;
        Assert.Equal((96, 4, "better"), (entry.BpmReached, entry.Rating, entry.Notes));
        Assert.Equal(_db.Clock.Now, entry.UpdatedAt);
        Assert.Equal(1, _db.Query(db => db.ProgressEntries.Count()));
    }

    [Fact]
    public void Out_of_range_values_are_dropped_and_notes_trimmed_and_capped()
    {
        _store.Save(Monday, _exercise, 0, 9, "  " + new string('x', 2_500));
        var entry = _store.Get(Monday, _exercise)!;
        Assert.Null(entry.BpmReached);
        Assert.Null(entry.Rating);
        Assert.Equal(Domain.ProgressEntry.MaxNotesLength, entry.Notes!.Length);
    }

    [Fact]
    public void Saving_nothing_deletes_the_entry()
    {
        _store.Save(Monday, _exercise, 100, null, null);
        _store.Save(Monday, _exercise, null, null, "   ");
        Assert.Null(_store.Get(Monday, _exercise));
        Assert.Equal(0, _db.Query(db => db.ProgressEntries.Count()));

        _store.Save(Monday, _other, null, null, null); // nothing to delete: no-op
    }

    [Fact]
    public void History_joins_practice_time_and_entries_by_day()
    {
        _db.Logs.Save(new PracticeKey(Monday, _exercise), new PracticeState(600, true));            // time only
        _db.Logs.Save(new PracticeKey(Monday.AddDays(1), _exercise), new PracticeState(300, false)); // time + entry
        _store.Save(Monday.AddDays(1), _exercise, 110, 4, "clean");
        _store.Save(Monday.AddDays(3), _exercise, 115, null, null);                                   // entry only
        _db.Logs.Save(new PracticeKey(Monday, _other), new PracticeState(900, true));                 // other exercise
        _db.Logs.Save(new PracticeKey(Monday.AddDays(2), _exercise), new PracticeState(0, false));    // reset to 0: no day

        var history = _store.History(_exercise);

        Assert.Equal([Monday, Monday.AddDays(1), Monday.AddDays(3)], history.Select(d => d.Date));
        Assert.Equal((600, (int?)null), (history[0].SecondsPractised, history[0].Bpm));
        Assert.Equal((300, (int?)110, (int?)4, "clean"), (history[1].SecondsPractised, history[1].Bpm, history[1].Rating, history[1].Notes));
        Assert.Equal((0, (int?)115), (history[2].SecondsPractised, history[2].Bpm));
        Assert.Equal(new HashSet<int> { _exercise, _other }, _store.SubcategoriesWithHistory());
    }

    [Fact]
    public void Loading_a_saved_week_keeps_progress_history()
    {
        _db.Logs.Save(new PracticeKey(Monday, _exercise), new PracticeState(600, true));
        _store.Save(Monday, _exercise, 120, 5, "personal best");
        var (plan, request) = _db.MakePlan(Monday);
        _db.Weeks.Save("Block A", plan, request, routineId: null);

        _db.Weeks.Load(_db.Weeks.ListSaved().Single().Id, Monday); // clears that week's practice time, by design

        var day = Assert.Single(_store.History(_exercise));
        Assert.Equal((0, (int?)120, "personal best"), (day.SecondsPractised, day.Bpm, day.Notes));
    }

    [Fact]
    public void Rating_and_bpm_are_constrained_in_the_database_too()
    {
        using var db = _db.CreateDbContext();
        db.ProgressEntries.Add(new Domain.ProgressEntry { Date = Monday, SubcategoryId = _exercise, Rating = 7 });
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }
}
