using GuitarPractice.Domain;
using GuitarPractice.Domain.Practice;
using GuitarPractice.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuitarPractice.Infrastructure;

/// <summary>
/// Per-block, per-day progress log (the exercises played with BPM and note value, a 1–5 rating, notes) and history
/// joined with practised time. Synchronous, like the other stores.
/// </summary>
public sealed class ProgressStore(IDbContextFactory<PracticeDbContext> contextFactory, TimeProvider clock)
{
    /// <summary>Raised (on the caller's thread) after <see cref="Save"/> with the subcategory id, so open views refresh.</summary>
    public event EventHandler<int>? Changed;

    /// <summary>The day's entry with its exercises (in logged order), or null.</summary>
    public ProgressEntry? Get(DateOnly date, int subcategoryId)
    {
        using var db = contextFactory.CreateDbContext();
        return db.ProgressEntries.AsNoTracking()
            .Include(e => e.Exercises.OrderBy(x => x.SortOrder))
            .SingleOrDefault(e => e.Date == date && e.SubcategoryId == subcategoryId);
    }

    /// <summary>
    /// Replaces the day's entry. Exercise lines are cleaned: names trimmed and capped at
    /// <see cref="ProgressExercise.MaxNameLength"/>, out-of-range BPMs dropped (1–400), lines with neither a name nor a
    /// BPM removed. Rating outside 1–5 is dropped; notes are trimmed and capped. An entry with nothing left is deleted.
    /// </summary>
    public void Save(DateOnly date, int subcategoryId, IEnumerable<ExerciseLog> exercises, int? rating, string? notes)
    {
        ArgumentNullException.ThrowIfNull(exercises);
        var lines = exercises
            .Select(x => new ExerciseLog(
                Clean(x.Name, ProgressExercise.MaxNameLength) ?? "",
                x.Bpm is >= ProgressExercise.MinBpm and <= ProgressExercise.MaxBpm ? x.Bpm : null,
                x.NoteValue is { } v && Enum.IsDefined(v) ? v : null,
                x.RemovedRun is >= 1 ? x.RemovedRun : null))
            .Where(x => x.Name.Length > 0 || x.Bpm is not null)
            .ToList();
        rating = rating is >= 1 and <= 5 ? rating : null;
        notes = Clean(notes, ProgressEntry.MaxNotesLength);

        using var db = contextFactory.CreateDbContext();
        var entry = db.ProgressEntries.Include(e => e.Exercises)
            .SingleOrDefault(e => e.Date == date && e.SubcategoryId == subcategoryId);
        if (lines.Count == 0 && rating is null && notes is null)
        {
            if (entry is not null)
            {
                db.ProgressEntries.Remove(entry); // exercises cascade
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
        db.ProgressExercises.RemoveRange(entry.Exercises);
        entry.Exercises.Clear();
        for (var i = 0; i < lines.Count; i++)
            entry.Exercises.Add(new ProgressExercise
            {
                Name = lines[i].Name, Bpm = lines[i].Bpm, NoteValue = lines[i].NoteValue, RemovedRun = lines[i].RemovedRun, SortOrder = i,
            });
        entry.Rating = rating;
        entry.Notes = notes;
        entry.UpdatedAt = clock.GetUtcNow();
        db.SaveChanges();
        Changed?.Invoke(this, subcategoryId);
    }

    /// <summary>Every day with practice time and/or a progress entry for the block, oldest first.</summary>
    public IReadOnlyList<ProgressDay> History(int subcategoryId)
    {
        using var db = contextFactory.CreateDbContext();
        var seconds = db.PracticeLogs.AsNoTracking()
            .Where(l => l.SubcategoryId == subcategoryId && l.ElapsedSeconds > 0)
            .Select(l => new { l.Date, l.ElapsedSeconds })
            .ToDictionary(l => l.Date, l => l.ElapsedSeconds);
        var entries = db.ProgressEntries.AsNoTracking()
            .Include(e => e.Exercises)
            .Where(e => e.SubcategoryId == subcategoryId)
            .AsSplitQuery()
            .ToDictionary(e => e.Date);

        return seconds.Keys.Union(entries.Keys)
            .Order()
            .Select(date =>
            {
                entries.TryGetValue(date, out var e);
                var lines = e?.Exercises.OrderBy(x => x.SortOrder).Select(x => new ExerciseLog(x.Name, x.Bpm, x.NoteValue, x.RemovedRun)).ToList() ?? [];
                return new ProgressDay(date, seconds.GetValueOrDefault(date), lines, e?.Rating, e?.Notes);
            })
            .ToList();
    }

    /// <summary>
    /// Removes an exercise (name + note value) from the block's current progress without deleting anything: its current
    /// lines are marked as a removed run (the next run number for that exercise), so they stay in the history and the day
    /// log, and logging the same name again starts a fresh run. Its own target is kept. Returns the lines marked (0 =
    /// nothing current to remove). Raises <see cref="Changed"/>.
    /// </summary>
    public int RemoveExercise(int subcategoryId, string? name, NoteValue? noteValue)
    {
        var key = ProgressStats.ExerciseKey(name, noteValue);
        using var db = contextFactory.CreateDbContext();
        var same = db.ProgressExercises
            .Where(x => x.ProgressEntry.SubcategoryId == subcategoryId && x.NoteValue == noteValue)
            .AsEnumerable()
            .Where(x => ProgressStats.ExerciseKey(x.Name, x.NoteValue) == key)
            .ToList();
        var current = same.Where(x => x.RemovedRun is null).ToList();
        if (current.Count == 0) return 0;

        var run = same.Max(x => x.RemovedRun ?? 0) + 1;
        foreach (var line in current) line.RemovedRun = run;
        db.SaveChanges();
        Changed?.Invoke(this, subcategoryId);
        return current.Count;
    }

    /// <summary>
    /// Renames an exercise in a block: every current line of (<paramref name="name"/>, <paramref name="noteValue"/>) gets
    /// <paramref name="newName"/> (cleaned like a saved name). If that name already exists with the same note value it's
    /// a merge: the lines join it. The own target moves with it unless the other exercise already has one (that one is
    /// kept). Removed runs keep their old name. Returns the lines renamed (0 = nothing to do). Raises <see cref="Changed"/>.
    /// </summary>
    public int RenameExercise(int subcategoryId, string? name, NoteValue? noteValue, string newName)
    {
        var cleaned = Clean(newName, ProgressExercise.MaxNameLength)
            ?? throw new ArgumentException("Give it a name.", nameof(newName));
        var oldKey = ProgressStats.ExerciseKey(name, noteValue);
        var newKey = ProgressStats.ExerciseKey(cleaned, noteValue);

        using var db = contextFactory.CreateDbContext();
        var current = db.ProgressExercises.Include(x => x.ProgressEntry)
            .Where(x => x.ProgressEntry.SubcategoryId == subcategoryId && x.NoteValue == noteValue && x.RemovedRun == null)
            .ToList();
        // A merge takes the other exercise's spelling (its most recent), not however the name was typed.
        if (oldKey != newKey && current.Where(x => ProgressStats.ExerciseKey(x.Name, x.NoteValue) == newKey)
                .OrderByDescending(x => x.ProgressEntry.Date).FirstOrDefault() is { } existing)
            cleaned = existing.Name;
        var lines = current.Where(x => ProgressStats.ExerciseKey(x.Name, x.NoteValue) == oldKey && x.Name != cleaned).ToList();
        if (lines.Count == 0) return 0;
        foreach (var line in lines) line.Name = cleaned;

        if (oldKey != newKey && db.ExerciseTargets.SingleOrDefault(t => t.SubcategoryId == subcategoryId && t.Key == oldKey) is { } target)
        {
            if (db.ExerciseTargets.Any(t => t.SubcategoryId == subcategoryId && t.Key == newKey)) db.ExerciseTargets.Remove(target);
            else target.Key = newKey;
        }
        db.SaveChanges();
        Changed?.Invoke(this, subcategoryId);
        return lines.Count;
    }

    /// <summary>Exercise names used in this block, most recently used first (for suggestions).</summary>
    public IReadOnlyList<string> ExerciseNames(int subcategoryId)
    {
        using var db = contextFactory.CreateDbContext();
        return db.ProgressExercises.AsNoTracking()
            .Where(x => x.ProgressEntry.SubcategoryId == subcategoryId && x.Name != "")
            .Select(x => new { x.Name, x.ProgressEntry.Date })
            .AsEnumerable()
            .GroupBy(x => x.Name.ToUpperInvariant())
            .OrderByDescending(g => g.Max(x => x.Date))
            .Select(g => g.OrderByDescending(x => x.Date).First().Name)
            .ToList();
    }

    /// <summary>The block's target BPM (Subcategory.TargetBpm, seeded for built-ins, editable).</summary>
    public int? BlockTarget(int subcategoryId)
    {
        using var db = contextFactory.CreateDbContext();
        return db.Subcategories.AsNoTracking().Where(s => s.Id == subcategoryId).Select(s => s.TargetBpm).SingleOrDefault();
    }

    /// <summary>Sets (or with null / out of range, clears) the block's target. Raises <see cref="Changed"/>.</summary>
    public void SetBlockTarget(int subcategoryId, int? targetBpm)
    {
        using var db = contextFactory.CreateDbContext();
        var subcategory = db.Subcategories.Single(s => s.Id == subcategoryId);
        subcategory.TargetBpm = targetBpm is >= ProgressExercise.MinBpm and <= ProgressExercise.MaxBpm ? targetBpm : null;
        db.SaveChanges();
        Changed?.Invoke(this, subcategoryId);
    }

    /// <summary>The user's own exercise targets in a block, by <see cref="ProgressStats.ExerciseKey"/>.</summary>
    public IReadOnlyDictionary<string, int> Targets(int subcategoryId)
    {
        using var db = contextFactory.CreateDbContext();
        return db.ExerciseTargets.AsNoTracking().Where(t => t.SubcategoryId == subcategoryId).ToDictionary(t => t.Key, t => t.TargetBpm);
    }

    /// <summary>Sets (or with null / out of range, removes) an exercise's own target. Raises <see cref="Changed"/>.</summary>
    public void SetTarget(int subcategoryId, string? name, NoteValue? noteValue, int? targetBpm)
    {
        var key = ProgressStats.ExerciseKey(name, noteValue);
        using var db = contextFactory.CreateDbContext();
        var row = db.ExerciseTargets.SingleOrDefault(t => t.SubcategoryId == subcategoryId && t.Key == key);
        if (targetBpm is not (>= ProgressExercise.MinBpm and <= ProgressExercise.MaxBpm))
        {
            if (row is null) return;
            db.ExerciseTargets.Remove(row);
        }
        else
        {
            if (row is null)
            {
                row = new ExerciseTarget { SubcategoryId = subcategoryId, Key = key };
                db.ExerciseTargets.Add(row);
            }
            row.TargetBpm = targetBpm.Value;
            row.UpdatedAt = clock.GetUtcNow();
        }
        db.SaveChanges();
        Changed?.Invoke(this, subcategoryId);
    }

    /// <summary>Subcategories that have any practice time or progress entry.</summary>
    public IReadOnlySet<int> SubcategoriesWithHistory()
    {
        using var db = contextFactory.CreateDbContext();
        return db.PracticeLogs.AsNoTracking().Where(l => l.ElapsedSeconds > 0).Select(l => l.SubcategoryId)
            .Union(db.ProgressEntries.AsNoTracking().Select(e => e.SubcategoryId))
            .ToHashSet();
    }

    private static string? Clean(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        return text.Length > maxLength ? text[..maxLength] : text;
    }
}
