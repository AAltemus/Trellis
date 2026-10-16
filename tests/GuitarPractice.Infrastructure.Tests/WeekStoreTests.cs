using GuitarPractice.Domain;
using Microsoft.EntityFrameworkCore;

namespace GuitarPractice.Infrastructure.Tests;

public sealed class WeekStoreTests : IDisposable
{
    private static readonly DateOnly ThisWeek = new(2026, 10, 12);
    private static readonly DateOnly NextWeek = ThisWeek.AddDays(7);

    private readonly TestDatabase _db = new();
    private readonly int _routineId;

    public WeekStoreTests() => _routineId = _db.Library.GetOrCreateDefaultRoutine().Id;

    public void Dispose() => _db.Dispose();

    /// <summary>A plan's content independent of dates/ids: (weekday offset, subcategory, order, minutes, pinned).</summary>
    private static List<(int Offset, int SubcategoryId, int Order, int Minutes, bool Pinned)> Shape(WeeklyPlan plan) =>
        plan.Days
            .SelectMany(d => d.Entries.Select(e => (d.Date.DayNumber - plan.WeekStart.DayNumber, e.SubcategoryId, e.Order, e.PlannedMinutes, e.IsPinned)))
            .OrderBy(x => x.Item1).ThenBy(x => x.Order)
            .ToList();

    private void LockWeek(DateOnly weekStart, int seed = 1)
    {
        var (plan, request) = _db.MakePlan(weekStart, seed);
        _db.Weeks.Lock(plan, request, _routineId);
    }

    private void Save(string name, int seed = 1)
    {
        var (plan, request) = _db.MakePlan(ThisWeek, seed);
        _db.Weeks.Save(name, plan, request, _routineId);
    }

    private int PlanCount(bool template) => _db.Query(db => db.WeeklyPlans.Count(p => p.IsTemplate == template));

    [Fact]
    public void Lock_stores_the_plan_and_LoadLocked_returns_it_with_navigation_loaded()
    {
        var (plan, request) = _db.MakePlan(ThisWeek, seed: 7);
        _db.Weeks.Lock(plan, request, _routineId);

        var locked = _db.Weeks.LoadLocked(ThisWeek);

        Assert.NotNull(locked);
        Assert.False(locked.IsTemplate);
        Assert.Equal(7, locked.Seed);
        Assert.Equal(_routineId, locked.RoutineId);
        Assert.Equal(_db.Clock.Now, locked.CreatedAt);
        Assert.Equal(Enumerable.Range(0, 7).Select(ThisWeek.AddDays), locked.Days.Select(d => d.Date)); // ordered
        Assert.All(locked.Days, d => Assert.Equal(d.Entries.OrderBy(e => e.Order), d.Entries));
        Assert.All(locked.Days.SelectMany(d => d.Entries), e => Assert.NotNull(e.Subcategory.Category.Name));
        Assert.Equal(plan.Days.Sum(d => d.TotalMinutes), locked.Days.Sum(d => d.Entries.Sum(e => e.PlannedMinutes)));
        Assert.Null(_db.Weeks.LoadLocked(NextWeek));
    }

    [Fact]
    public void Locking_again_replaces_the_locked_plan_for_that_week_only()
    {
        LockWeek(NextWeek, seed: 99);
        LockWeek(ThisWeek, seed: 1);
        LockWeek(ThisWeek, seed: 2);

        Assert.Equal(2, PlanCount(template: false));
        Assert.Equal(2, _db.Weeks.LoadLocked(ThisWeek)!.Seed);
        Assert.Equal(99, _db.Weeks.LoadLocked(NextWeek)!.Seed);
    }

    [Fact]
    public void Unlock_removes_the_week_and_cascades_its_days()
    {
        LockWeek(ThisWeek);
        LockWeek(NextWeek);

        _db.Weeks.Unlock(ThisWeek);

        Assert.Null(_db.Weeks.LoadLocked(ThisWeek));
        Assert.NotNull(_db.Weeks.LoadLocked(NextWeek));
        Assert.Equal(7, _db.Query(db => db.PlanDays.Count()));
        Assert.Equal(
            _db.Weeks.LoadLocked(NextWeek)!.Days.Sum(d => d.Entries.Count),
            _db.Query(db => db.PlanEntries.Count()));

        _db.Weeks.Unlock(ThisWeek); // unlocking an unlocked week is a no-op
    }

    [Fact]
    public void Save_with_an_existing_name_replaces_it()
    {
        Save("Shred block", seed: 1);
        Save("  Shred block  ", seed: 2);
        Save("Legato week", seed: 3);

        var saved = _db.Weeks.ListSaved();
        Assert.Equal(["Legato week", "Shred block"], saved.Select(s => s.Name));
        Assert.Equal(2, _db.Query(db => db.WeeklyPlans.Single(p => p.Name == "Shred block").Seed));
        Assert.All(saved, s => Assert.Equal((7, 7 * 60), (s.PracticeDays, s.TotalMinutes)));
        Assert.Equal(0, PlanCount(template: false)); // saving never locks
    }

    [Fact]
    public void Save_rejects_blank_names_and_trims_long_ones()
    {
        var (plan, request) = _db.MakePlan(ThisWeek);
        Assert.Throws<ArgumentException>(() => _db.Weeks.Save("   ", plan, request, _routineId));

        _db.Weeks.Save(new string('x', 150), plan, request, _routineId);
        Assert.Equal(100, _db.Weeks.ListSaved().Single().Name.Length);
    }

    [Fact]
    public void SaveLocked_copies_the_locked_week_and_keeps_it_locked()
    {
        Assert.Throws<InvalidOperationException>(() => _db.Weeks.SaveLocked("Nope", ThisWeek));

        LockWeek(ThisWeek, seed: 5);
        _db.Weeks.SaveLocked("Frozen", ThisWeek);
        _db.Weeks.SaveLocked("Frozen", ThisWeek); // same name replaces

        var locked = _db.Weeks.LoadLocked(ThisWeek)!;
        var saved = _db.Query(db => db.WeeklyPlans.AsNoTracking()
            .Include(p => p.Days).ThenInclude(d => d.Entries)
            .Single(p => p.IsTemplate));
        Assert.Equal("Frozen", saved.Name);
        Assert.Equal(5, saved.Seed);
        Assert.Equal(Shape(locked), Shape(saved));
        Assert.Equal(1, PlanCount(template: false));
    }

    [Fact]
    public void Load_into_an_unlocked_week_locks_it_and_shifts_dates()
    {
        // Saved from an earlier week, loaded into this one.
        var (plan, request) = _db.MakePlan(ThisWeek.AddDays(-21), seed: 3);
        _db.Weeks.Save("Block A", plan, request, _routineId);
        var savedId = _db.Weeks.ListSaved().Single().Id;

        _db.Weeks.Load(savedId, ThisWeek);

        var locked = _db.Weeks.LoadLocked(ThisWeek)!;
        Assert.Equal(1, PlanCount(template: false));
        Assert.Equal("Block A", locked.Name);
        Assert.Equal(3, locked.Seed);
        Assert.Equal(Enumerable.Range(0, 7).Select(ThisWeek.AddDays), locked.Days.Select(d => d.Date));
        var saved = _db.Query(db => db.WeeklyPlans.AsNoTracking()
            .Include(p => p.Days).ThenInclude(d => d.Entries)
            .Single(p => p.Id == savedId));
        Assert.Equal(Shape(saved), Shape(locked));
        Assert.Equal(1, PlanCount(template: true)); // the saved week stays
    }

    [Fact]
    public void Load_into_a_locked_week_is_refused_and_changes_nothing()
    {
        Save("Block A", seed: 3);
        var savedId = _db.Weeks.ListSaved().Single().Id;
        LockWeek(ThisWeek, seed: 8);
        var key = new PracticeKey(ThisWeek, _db.MakePlan(ThisWeek).Plan.Days[0].Entries[0].TaskId);
        _db.Logs.Save(key, new PracticeState(90, Completed: false));

        var error = Assert.Throws<InvalidOperationException>(() => _db.Weeks.Load(savedId, ThisWeek));

        Assert.Contains("locked", error.Message, StringComparison.Ordinal);
        var locked = _db.Weeks.LoadLocked(ThisWeek)!;
        Assert.Equal(8, locked.Seed);
        Assert.Null(locked.Name);
        Assert.Equal(1, PlanCount(template: false));
        Assert.Equal(new PracticeState(90, false), _db.Logs.Load(ThisWeek, ThisWeek.AddDays(6))[key]); // times kept

        _db.Weeks.Unlock(ThisWeek); // unlocking first makes it loadable
        _db.Weeks.Load(savedId, ThisWeek);
        Assert.Equal("Block A", _db.Weeks.LoadLocked(ThisWeek)!.Name);
    }

    [Fact]
    public void Load_into_next_week_leaves_this_week_alone()
    {
        Save("Block A", seed: 3);
        var savedId = _db.Weeks.ListSaved().Single().Id;
        LockWeek(ThisWeek, seed: 8);

        _db.Weeks.Load(savedId, NextWeek);
        Assert.Throws<InvalidOperationException>(() => _db.Weeks.Load(savedId, NextWeek)); // now locked: no overwrite

        Assert.Equal(8, _db.Weeks.LoadLocked(ThisWeek)!.Seed);
        var next = _db.Weeks.LoadLocked(NextWeek)!;
        Assert.Equal("Block A", next.Name);
        Assert.Equal(NextWeek, next.Days[0].Date);
        Assert.Equal(2, PlanCount(template: false));
    }

    [Fact]
    public void Two_locked_weeks_may_share_a_saved_name_but_two_saved_weeks_may_not()
    {
        Save("Block A");
        var savedId = _db.Weeks.ListSaved().Single().Id;

        _db.Weeks.Load(savedId, ThisWeek);
        _db.Weeks.Load(savedId, NextWeek); // filtered unique index: locked rows aren't constrained

        Assert.Equal(2, _db.Query(db => db.WeeklyPlans.Count(p => !p.IsTemplate && p.Name == "Block A")));

        using var db = _db.CreateDbContext();
        db.WeeklyPlans.Add(new WeeklyPlan { Name = "Block A", IsTemplate = true, WeekStart = ThisWeek });
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public void DeleteSaved_only_deletes_saved_weeks()
    {
        Save("Block A");
        LockWeek(ThisWeek);
        var savedId = _db.Weeks.ListSaved().Single().Id;
        var lockedId = _db.Weeks.LoadLocked(ThisWeek)!.Id;

        _db.Weeks.DeleteSaved(lockedId);
        Assert.NotNull(_db.Weeks.LoadLocked(ThisWeek));

        _db.Weeks.DeleteSaved(savedId);
        Assert.Empty(_db.Weeks.ListSaved());
        Assert.NotNull(_db.Weeks.LoadLocked(ThisWeek));
    }

    [Fact]
    public void Practice_logs_survive_lock_unlock_save_and_delete()
    {
        var subcategoryId = _db.MakePlan(ThisWeek).Plan.Days[0].Entries[0].TaskId;
        var key = new PracticeKey(ThisWeek, subcategoryId);
        _db.Logs.Save(key, new PracticeState(240, Completed: true));

        LockWeek(ThisWeek);
        _db.Weeks.SaveLocked("Block A", ThisWeek);
        _db.Weeks.Unlock(ThisWeek);
        var savedId = _db.Weeks.ListSaved().Single().Id;
        _db.Weeks.Load(savedId, NextWeek); // loading into another week leaves this week's logs alone
        Save("Block A", seed: 4);
        _db.Weeks.DeleteSaved(_db.Weeks.ListSaved().Single().Id);
        _db.Weeks.Unlock(NextWeek);

        Assert.Equal(new PracticeState(240, true), _db.Logs.Load(ThisWeek, ThisWeek.AddDays(6))[key]);
    }

    [Fact]
    public void Saving_does_not_copy_times_and_loading_starts_the_target_week_fresh()
    {
        var subcategoryId = _db.MakePlan(ThisWeek).Plan.Days[0].Entries[0].TaskId;
        var lastWeek = ThisWeek.AddDays(-7);
        foreach (var date in new[] { lastWeek.AddDays(6), ThisWeek, ThisWeek.AddDays(6), NextWeek, NextWeek.AddDays(3) })
            _db.Logs.Save(new PracticeKey(date, subcategoryId), new PracticeState(120, Completed: true));

        LockWeek(ThisWeek);
        _db.Weeks.SaveLocked("Block A", ThisWeek);
        Assert.Equal(5, _db.Query(db => db.PracticeLogs.Count())); // saving copies the plan, never times

        var savedId = _db.Weeks.ListSaved().Single().Id;
        _db.Weeks.Unlock(ThisWeek);
        _db.Weeks.Load(savedId, ThisWeek);

        Assert.Empty(_db.Logs.Load(ThisWeek, ThisWeek.AddDays(6)));            // both ends of the week cleared
        Assert.Single(_db.Logs.Load(lastWeek, lastWeek.AddDays(6)));           // neighbours untouched
        Assert.Equal(2, _db.Logs.Load(NextWeek, NextWeek.AddDays(6)).Count);

        _db.Weeks.Load(savedId, NextWeek);
        Assert.Empty(_db.Logs.Load(NextWeek, NextWeek.AddDays(6)));
        Assert.Single(_db.Logs.Load(lastWeek, lastWeek.AddDays(6)));
    }
}
