using GuitarPractice.Domain;
using GuitarPractice.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuitarPractice.Infrastructure;

public sealed record SongDetails(
    string? Artist,
    int? TuningId,
    SongStatus Status = SongStatus.Learning,
    string? SongKey = null,
    string? ReferenceUrl = null,
    int? TargetBpm = null);

/// <summary>
/// Library + routine persistence for the UI. Each call uses its own short-lived context (desktop app, no request
/// scope). Synchronous on purpose: operations are tiny local SQLite writes, and blocking on async EF calls under
/// Avalonia's SynchronizationContext is a deadlock risk.
/// </summary>
public sealed class LibraryStore(IDbContextFactory<PracticeDbContext> contextFactory)
{
    private static readonly string[] Palette =
        ["#7CB342", "#26A69A", "#5C6BC0", "#EC407A", "#FFA726", "#8D6E63", "#78909C", "#AB47BC"];

    public IReadOnlyList<Category> LoadLibrary()
    {
        using var db = contextFactory.CreateDbContext();
        return db.Categories.AsNoTracking()
            .Where(c => !c.IsArchived)
            .Include(c => c.Subcategories.Where(s => !s.IsArchived).OrderBy(s => s.SortOrder).ThenBy(s => s.Id))
            .ThenInclude(s => s.Tuning)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .AsSplitQuery()
            .ToList();
    }

    public IReadOnlyList<Tuning> LoadTunings()
    {
        using var db = contextFactory.CreateDbContext();
        return db.Tunings.AsNoTracking().OrderBy(t => t.SortOrder).ThenBy(t => t.Id).ToList();
    }

    /// <summary>
    /// Returns the first routine, creating "My routine" on first run: 60 min/day, first warmup included,
    /// and the first subcategory of every exercise category at weight 5.
    /// </summary>
    public Routine GetOrCreateDefaultRoutine()
    {
        using var db = contextFactory.CreateDbContext();
        var routine = db.Routines.AsNoTracking()
            .Include(r => r.Items)
            .Include(r => r.DayBudgets)
            .OrderBy(r => r.Id)
            .AsSplitQuery()
            .FirstOrDefault();
        if (routine is not null) return routine;

        routine = new Routine { Name = "My routine" };
        foreach (var day in Enum.GetValues<DayOfWeek>())
            routine.DayBudgets.Add(new RoutineDayBudget { Day = day, Minutes = 60 });

        var starters = db.Subcategories
            .Where(s => s.IsBuiltIn && !s.IsArchived && s.Category.Kind == CategoryKind.Exercise)
            .OrderBy(s => s.Category.SortOrder).ThenBy(s => s.SortOrder).ThenBy(s => s.Id)
            .Select(s => new { s.Id, s.CategoryId, s.Pin })
            .ToList()
            .GroupBy(s => s.CategoryId)
            .Select(g => g.First());
        foreach (var s in starters)
            routine.Items.Add(new RoutineItem { SubcategoryId = s.Id, Weight = s.Pin == PinMode.None ? 5 : 1 });

        db.Routines.Add(routine);
        db.SaveChanges();
        return routine;
    }

    /// <summary>Upserts a weight; 0 removes the subcategory from the routine.</summary>
    public void SetWeight(int routineId, int subcategoryId, int weight)
    {
        using var db = contextFactory.CreateDbContext();
        var item = db.RoutineItems.Find(routineId, subcategoryId);
        if (weight <= 0)
        {
            if (item is not null) db.RoutineItems.Remove(item);
        }
        else if (item is null)
        {
            db.RoutineItems.Add(new RoutineItem { RoutineId = routineId, SubcategoryId = subcategoryId, Weight = Math.Min(weight, 10) });
        }
        else
        {
            item.Weight = Math.Min(weight, 10);
        }
        db.SaveChanges();
    }

    /// <summary>The same minutes on every day.</summary>
    public void SaveSettings(int routineId, int minutesPerDay, bool pinnedTimeOnTop) =>
        SaveSettings(routineId, Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => minutesPerDay), pinnedTimeOnTop);

    /// <summary>
    /// Minutes per weekday (0 = rest day) and the warmup budget mode. Days missing from
    /// <paramref name="minutesByDay"/> keep their stored value; negative minutes are stored as 0.
    /// </summary>
    public void SaveSettings(int routineId, IReadOnlyDictionary<DayOfWeek, int> minutesByDay, bool pinnedTimeOnTop)
    {
        ArgumentNullException.ThrowIfNull(minutesByDay);
        using var db = contextFactory.CreateDbContext();
        var routine = db.Routines.Include(r => r.DayBudgets).Single(r => r.Id == routineId);
        routine.PinnedTimeOnTopOfBudget = pinnedTimeOnTop;
        foreach (var (day, minutes) in minutesByDay)
        {
            var value = Math.Max(0, minutes);
            var budget = routine.DayBudgets.FirstOrDefault(b => b.Day == day);
            if (budget is null) routine.DayBudgets.Add(new RoutineDayBudget { Day = day, Minutes = value });
            else budget.Minutes = value;
        }
        db.SaveChanges();
    }

    public void SaveSeed(int routineId, int seed)
    {
        using var db = contextFactory.CreateDbContext();
        var routine = db.Routines.Single(r => r.Id == routineId);
        routine.PlanSeed = seed;
        db.SaveChanges();
    }

    public Category AddCategory(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var db = contextFactory.CreateDbContext();
        var count = db.Categories.Count();
        var category = new Category
        {
            Name = name.Trim(),
            SortOrder = (db.Categories.Max(c => (int?)c.SortOrder) ?? 0) + 10,
            ColorHex = Palette[count % Palette.Length],
        };
        db.Categories.Add(category);
        db.SaveChanges();
        return category;
    }

    /// <summary>Adds a user subcategory. Pass <paramref name="song"/> for Songs categories.</summary>
    public Subcategory AddSubcategory(int categoryId, string name, SongDetails? song = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var db = contextFactory.CreateDbContext();
        var subcategory = new Subcategory
        {
            CategoryId = categoryId,
            Name = name.Trim(),
            SortOrder = (db.Subcategories.Where(s => s.CategoryId == categoryId).Max(s => (int?)s.SortOrder) ?? 0) + 1,
            MinSessionMinutes = song is null ? 10 : 15,
            MaxSessionMinutes = song is null ? 20 : 30,
        };
        if (song is not null)
        {
            subcategory.Artist = string.IsNullOrWhiteSpace(song.Artist) ? null : song.Artist.Trim();
            subcategory.TuningId = song.TuningId;
            subcategory.Status = song.Status;
            subcategory.SongKey = song.SongKey;
            subcategory.ReferenceUrl = song.ReferenceUrl;
            subcategory.TargetBpm = song.TargetBpm;
        }
        db.Subcategories.Add(subcategory);
        db.SaveChanges();
        return subcategory;
    }

    /// <summary>
    /// Subcategories removed from the library (archived) whose category is still there, in library order, with their
    /// category and tuning loaded.
    /// </summary>
    public IReadOnlyList<Subcategory> LoadRemoved()
    {
        using var db = contextFactory.CreateDbContext();
        return db.Subcategories.AsNoTracking()
            .Include(s => s.Category)
            .Include(s => s.Tuning)
            .Where(s => s.IsArchived && !s.Category.IsArchived)
            .OrderBy(s => s.Category.SortOrder).ThenBy(s => s.Category.Id).ThenBy(s => s.SortOrder).ThenBy(s => s.Id)
            .ToList();
    }

    /// <summary>
    /// Puts a removed subcategory back in the library (with its history, which was never touched). It is not re-added to
    /// any routine: weight 0 until the user sets one. Returns it with its tuning loaded.
    /// </summary>
    public Subcategory Restore(int subcategoryId)
    {
        using var db = contextFactory.CreateDbContext();
        var subcategory = db.Subcategories.Include(s => s.Tuning).Single(s => s.Id == subcategoryId);
        subcategory.IsArchived = false;
        db.SaveChanges();
        return subcategory;
    }

    /// <summary>Soft-deletes a subcategory and removes it from every routine. Past plans keep their history.</summary>
    public void Archive(int subcategoryId)
    {
        using var db = contextFactory.CreateDbContext();
        var subcategory = db.Subcategories.Single(s => s.Id == subcategoryId);
        subcategory.IsArchived = true;
        db.RoutineItems.RemoveRange(db.RoutineItems.Where(i => i.SubcategoryId == subcategoryId));
        db.SaveChanges();
    }
}
