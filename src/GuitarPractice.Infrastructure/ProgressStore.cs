using GuitarPractice.Domain;
using GuitarPractice.Domain.Practice;
using GuitarPractice.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuitarPractice.Infrastructure;

/// <summary>
/// Per-exercise, per-day progress log (BPM reached, 1–5 rating, notes) and history joined with practised time.
/// Synchronous, like the other stores.
/// </summary>
public sealed class ProgressStore(IDbContextFactory<PracticeDbContext> contextFactory, TimeProvider clock)
{
    /// <summary>Raised (on the caller's thread) after <see cref="Save"/> with the subcategory id, so open views refresh.</summary>
    public event EventHandler<int>? Changed;

    public ProgressEntry? Get(DateOnly date, int subcategoryId)
    {
        using var db = contextFactory.CreateDbContext();
        return db.ProgressEntries.AsNoTracking().SingleOrDefault(e => e.Date == date && e.SubcategoryId == subcategoryId);
    }

    /// <summary>
    /// Upserts the day's entry. Out-of-range values are dropped (BPM 1–400, rating 1–5), notes are trimmed and capped at
    /// <see cref="ProgressEntry.MaxNotesLength"/>; an entry with nothing left is deleted.
    /// </summary>
    public void Save(DateOnly date, int subcategoryId, int? bpm, int? rating, string? notes)
    {
        bpm = bpm is >= ProgressEntry.MinBpm and <= ProgressEntry.MaxBpm ? bpm : null;
        rating = rating is >= 1 and <= 5 ? rating : null;
        notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (notes is { Length: > ProgressEntry.MaxNotesLength }) notes = notes[..ProgressEntry.MaxNotesLength];

        using var db = contextFactory.CreateDbContext();
        var entry = db.ProgressEntries.SingleOrDefault(e => e.Date == date && e.SubcategoryId == subcategoryId);
        if (bpm is null && rating is null && notes is null)
        {
            if (entry is not null)
            {
                db.ProgressEntries.Remove(entry);
                db.SaveChanges();
                Changed?.Invoke(this, subcategoryId);
            }
            return;
        }

        if (entry is null)
        {
            entry = new ProgressEntry { Date = date, SubcategoryId = subcategoryId };
            db.ProgressEntries.Add(entry);
        }
        entry.BpmReached = bpm;
        entry.Rating = rating;
        entry.Notes = notes;
        entry.UpdatedAt = clock.GetUtcNow();
        db.SaveChanges();
        Changed?.Invoke(this, subcategoryId);
    }

    /// <summary>Every day with practice time and/or a progress entry for the exercise, oldest first.</summary>
    public IReadOnlyList<ProgressDay> History(int subcategoryId)
    {
        using var db = contextFactory.CreateDbContext();
        var seconds = db.PracticeLogs.AsNoTracking()
            .Where(l => l.SubcategoryId == subcategoryId && l.ElapsedSeconds > 0)
            .Select(l => new { l.Date, l.ElapsedSeconds })
            .ToDictionary(l => l.Date, l => l.ElapsedSeconds);
        var entries = db.ProgressEntries.AsNoTracking()
            .Where(e => e.SubcategoryId == subcategoryId)
            .ToDictionary(e => e.Date);

        return seconds.Keys.Union(entries.Keys)
            .Order()
            .Select(date =>
            {
                entries.TryGetValue(date, out var e);
                return new ProgressDay(date, seconds.GetValueOrDefault(date), e?.BpmReached, e?.Rating, e?.Notes);
            })
            .ToList();
    }

    /// <summary>Subcategories that have any practice time or progress entry.</summary>
    public IReadOnlySet<int> SubcategoriesWithHistory()
    {
        using var db = contextFactory.CreateDbContext();
        return db.PracticeLogs.AsNoTracking().Where(l => l.ElapsedSeconds > 0).Select(l => l.SubcategoryId)
            .Union(db.ProgressEntries.AsNoTracking().Select(e => e.SubcategoryId))
            .ToHashSet();
    }
}
