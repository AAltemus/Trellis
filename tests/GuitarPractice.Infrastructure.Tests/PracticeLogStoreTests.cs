using Microsoft.EntityFrameworkCore;

namespace GuitarPractice.Infrastructure.Tests;

public sealed class PracticeLogStoreTests : IDisposable
{
    private static readonly DateOnly Monday = new(2026, 10, 12);

    private readonly TestDatabase _db = new();
    private readonly int _subcategoryId;
    private readonly int _otherSubcategoryId;

    public PracticeLogStoreTests()
    {
        var ids = _db.Query(db => db.Subcategories.OrderBy(s => s.Id).Select(s => s.Id).Take(2).ToList());
        _subcategoryId = ids[0];
        _otherSubcategoryId = ids[1];
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Save_upserts_one_row_per_date_and_subcategory()
    {
        var key = new PracticeKey(Monday, _subcategoryId);

        _db.Logs.Save(key, new PracticeState(30, Completed: false));
        var later = _db.Clock.Now.AddMinutes(5);
        _db.Clock.Now = later;
        _db.Logs.Save(key, new PracticeState(300, Completed: true));

        var rows = _db.Query(db => db.PracticeLogs.AsNoTracking().ToList());
        var row = Assert.Single(rows);
        Assert.Equal(300, row.ElapsedSeconds);
        Assert.True(row.Completed);
        Assert.Equal(later, row.UpdatedAt);
        Assert.Equal(new PracticeState(300, true), _db.Logs.Load(Monday, Monday)[key]);
    }

    [Fact]
    public void Save_clamps_negative_elapsed()
    {
        var key = new PracticeKey(Monday, _subcategoryId);
        _db.Logs.Save(key, new PracticeState(-5, Completed: false));
        Assert.Equal(0, _db.Logs.Load(Monday, Monday)[key].ElapsedSeconds);
    }

    [Fact]
    public void Same_subcategory_on_different_days_is_separate()
    {
        _db.Logs.Save(new PracticeKey(Monday, _subcategoryId), new PracticeState(60, false));
        _db.Logs.Save(new PracticeKey(Monday.AddDays(1), _subcategoryId), new PracticeState(120, false));
        _db.Logs.Save(new PracticeKey(Monday, _otherSubcategoryId), new PracticeState(180, false));

        Assert.Equal(3, _db.Logs.Load(Monday, Monday.AddDays(6)).Count);
    }

    [Fact]
    public void Load_range_is_inclusive_on_both_ends()
    {
        for (var day = -1; day <= 7; day++)
            _db.Logs.Save(new PracticeKey(Monday.AddDays(day), _subcategoryId), new PracticeState(day + 10, false));

        var week = _db.Logs.Load(Monday, Monday.AddDays(6));

        Assert.Equal(7, week.Count);
        Assert.Contains(new PracticeKey(Monday, _subcategoryId), week.Keys);
        Assert.Contains(new PracticeKey(Monday.AddDays(6), _subcategoryId), week.Keys);
        Assert.DoesNotContain(new PracticeKey(Monday.AddDays(-1), _subcategoryId), week.Keys);
        Assert.DoesNotContain(new PracticeKey(Monday.AddDays(7), _subcategoryId), week.Keys);
    }

    [Fact]
    public void Reset_deletes_only_that_key_and_tolerates_missing_rows()
    {
        var key = new PracticeKey(Monday, _subcategoryId);
        var other = new PracticeKey(Monday, _otherSubcategoryId);
        _db.Logs.Save(key, new PracticeState(60, true));
        _db.Logs.Save(other, new PracticeState(90, false));

        _db.Logs.Reset(key);
        _db.Logs.Reset(key);
        _db.Logs.Reset(new PracticeKey(Monday.AddDays(3), _subcategoryId));

        var left = _db.Logs.Load(Monday, Monday.AddDays(6));
        Assert.Equal([other], left.Keys);
    }

    [Fact]
    public void Duplicate_date_and_subcategory_is_rejected_by_the_unique_index()
    {
        using var db = _db.CreateDbContext();
        db.PracticeLogs.Add(new Domain.PracticeLog { Date = Monday, SubcategoryId = _subcategoryId });
        db.PracticeLogs.Add(new Domain.PracticeLog { Date = Monday, SubcategoryId = _subcategoryId });
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }
}
