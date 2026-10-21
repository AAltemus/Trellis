using GuitarPractice.Domain;
using GuitarPractice.Domain.Planning;
using GuitarPractice.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuitarPractice.Infrastructure;

public sealed record SavedWeekInfo(int Id, string Name, DateOnly SavedFromWeek, int TotalMinutes, int PracticeDays);

/// <summary>
/// Locked weeks (one frozen plan per week) and saved weeks (named copies that can be loaded into any week).
/// Both are <see cref="WeeklyPlan"/> rows; see that type for the distinction.
/// </summary>
public sealed class WeekStore(IDbContextFactory<PracticeDbContext> contextFactory, TimeProvider clock)
{
    /// <summary>The locked plan for a week, with subcategory + category loaded for display; null when unlocked.</summary>
    public WeeklyPlan? LoadLocked(DateOnly weekStart)
    {
        using var db = contextFactory.CreateDbContext();
        return WithDays(db.WeeklyPlans.AsNoTracking())
            .Where(p => !p.IsTemplate && p.WeekStart == weekStart)
            .OrderByDescending(p => p.Id)
            .FirstOrDefault();
    }

    /// <summary>Freezes a generated plan as this week's plan.</summary>
    public void Lock(PlanResult plan, PlanRequest request, int? routineId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var db = contextFactory.CreateDbContext();
        RemoveLocked(db, plan.WeekStart);
        db.WeeklyPlans.Add(plan.ToWeeklyPlan(request, routineId, clock.GetUtcNow()));
        db.SaveChanges();
    }

    /// <summary>
    /// Swaps two items of the week's locked plan (see <see cref="PlanSwap"/>): the exercises trade places, each slot
    /// keeps its day, position and minutes. Returns why not when refused (nothing changes then).
    /// </summary>
    public SwapRefusal Swap(DateOnly weekStart, (DateOnly Date, int SubcategoryId) a, (DateOnly Date, int SubcategoryId) b)
    {
        using var db = contextFactory.CreateDbContext();
        var plan = db.WeeklyPlans
            .Include(p => p.Days).ThenInclude(d => d.Entries)
            .Where(p => !p.IsTemplate && p.WeekStart == weekStart)
            .OrderByDescending(p => p.Id)
            .FirstOrDefault();
        if (plan is null) return SwapRefusal.NotFound;
        var refusal = PlanSwap.Check(plan, a, b);
        if (refusal != SwapRefusal.None) return refusal;
        PlanSwap.Apply(plan, a, b);
        db.SaveChanges();
        return SwapRefusal.None;
    }

    public void Unlock(DateOnly weekStart)
    {
        using var db = contextFactory.CreateDbContext();
        RemoveLocked(db, weekStart);
        db.SaveChanges();
    }

    public IReadOnlyList<SavedWeekInfo> ListSaved()
    {
        using var db = contextFactory.CreateDbContext();
        return db.WeeklyPlans.AsNoTracking()
            .Where(p => p.IsTemplate)
            .OrderBy(p => p.Name)
            .Select(p => new SavedWeekInfo(
                p.Id,
                p.Name ?? "Untitled",
                p.WeekStart,
                p.Days.Sum(d => d.PinnedMinutes + d.FlexMinutes),
                p.Days.Count(d => d.BudgetMinutes > 0)))
            .ToList();
    }

    /// <summary>Saves a generated (unlocked) week under a name. An existing saved week with that name is replaced.</summary>
    public void Save(string name, PlanResult plan, PlanRequest request, int? routineId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        name = CleanName(name);
        using var db = contextFactory.CreateDbContext();
        RemoveSaved(db, name);
        var saved = plan.ToWeeklyPlan(request, routineId, clock.GetUtcNow());
        saved.IsTemplate = true;
        saved.Name = name;
        db.WeeklyPlans.Add(saved);
        db.SaveChanges();
    }

    /// <summary>Saves this week's locked plan under a name. An existing saved week with that name is replaced.</summary>
    public void SaveLocked(string name, DateOnly weekStart)
    {
        name = CleanName(name);
        using var db = contextFactory.CreateDbContext();
        var locked = WithDays(db.WeeklyPlans.AsNoTracking())
            .Where(p => !p.IsTemplate && p.WeekStart == weekStart)
            .OrderByDescending(p => p.Id)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("This week isn't locked.");
        RemoveSaved(db, name);
        db.WeeklyPlans.Add(locked.CloneTo(weekStart, isTemplate: true, name, clock.GetUtcNow()));
        db.SaveChanges();
    }

    /// <summary>
    /// Copies a saved week onto an unlocked <paramref name="weekStart"/> and locks it there. A locked week is never
    /// overwritten: unlock it first (user requirement, 2026-10-16). The week starts fresh: practice logs on its seven
    /// dates are deleted.
    /// </summary>
    /// <exception cref="InvalidOperationException">The target week is locked.</exception>
    public void Load(int savedWeekId, DateOnly weekStart)
    {
        using var db = contextFactory.CreateDbContext();
        if (db.WeeklyPlans.Any(p => !p.IsTemplate && p.WeekStart == weekStart))
            throw new InvalidOperationException($"The week of {weekStart:MMM d} is locked. Unlock it before loading a saved week into it.");
        var saved = WithDays(db.WeeklyPlans.AsNoTracking()).Single(p => p.Id == savedWeekId && p.IsTemplate);
        var weekEnd = weekStart.AddDays(6);
        db.PracticeLogs.RemoveRange(db.PracticeLogs.Where(l => l.Date >= weekStart && l.Date <= weekEnd));
        db.WeeklyPlans.Add(saved.CloneTo(weekStart, isTemplate: false, saved.Name, clock.GetUtcNow()));
        db.SaveChanges();
    }

    public void DeleteSaved(int savedWeekId)
    {
        using var db = contextFactory.CreateDbContext();
        db.WeeklyPlans.RemoveRange(db.WeeklyPlans.Where(p => p.Id == savedWeekId && p.IsTemplate));
        db.SaveChanges();
    }

    private static IQueryable<WeeklyPlan> WithDays(IQueryable<WeeklyPlan> query) =>
        query
            .Include(p => p.Days.OrderBy(d => d.Date))
                .ThenInclude(d => d.Entries.OrderBy(e => e.Order))
                .ThenInclude(e => e.Subcategory)
                .ThenInclude(s => s.Category)
            .AsSplitQuery();

    // Days and entries go with the plan through ON DELETE CASCADE.
    private static void RemoveLocked(PracticeDbContext db, DateOnly weekStart) =>
        db.WeeklyPlans.RemoveRange(db.WeeklyPlans.Where(p => !p.IsTemplate && p.WeekStart == weekStart));

    private static void RemoveSaved(PracticeDbContext db, string name) =>
        db.WeeklyPlans.RemoveRange(db.WeeklyPlans.Where(p => p.IsTemplate && p.Name == name));

    private static string CleanName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();
        return name.Length > 100 ? name[..100] : name;
    }
}
