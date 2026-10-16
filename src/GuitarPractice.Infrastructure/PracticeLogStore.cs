using GuitarPractice.Domain;
using GuitarPractice.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuitarPractice.Infrastructure;

public readonly record struct PracticeKey(DateOnly Date, int SubcategoryId);

public readonly record struct PracticeState(int ElapsedSeconds, bool Completed);

/// <summary>Reads and writes practised time per (date, subcategory). Synchronous, like <see cref="LibraryStore"/>.</summary>
public sealed class PracticeLogStore(IDbContextFactory<PracticeDbContext> contextFactory, TimeProvider clock)
{
    public Dictionary<PracticeKey, PracticeState> Load(DateOnly from, DateOnly to)
    {
        using var db = contextFactory.CreateDbContext();
        return db.PracticeLogs.AsNoTracking()
            .Where(l => l.Date >= from && l.Date <= to)
            .ToDictionary(
                l => new PracticeKey(l.Date, l.SubcategoryId),
                l => new PracticeState(l.ElapsedSeconds, l.Completed));
    }

    public void Save(PracticeKey key, PracticeState state)
    {
        using var db = contextFactory.CreateDbContext();
        var log = db.PracticeLogs.SingleOrDefault(l => l.Date == key.Date && l.SubcategoryId == key.SubcategoryId);
        if (log is null)
        {
            log = new PracticeLog { Date = key.Date, SubcategoryId = key.SubcategoryId };
            db.PracticeLogs.Add(log);
        }
        log.ElapsedSeconds = Math.Max(0, state.ElapsedSeconds);
        log.Completed = state.Completed;
        log.UpdatedAt = clock.GetUtcNow();
        db.SaveChanges();
    }

    public void Reset(PracticeKey key)
    {
        using var db = contextFactory.CreateDbContext();
        var log = db.PracticeLogs.SingleOrDefault(l => l.Date == key.Date && l.SubcategoryId == key.SubcategoryId);
        if (log is null) return;
        db.PracticeLogs.Remove(log);
        db.SaveChanges();
    }
}
