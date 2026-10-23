using System.Text.Json;

namespace GuitarPractice.Domain.Planning;

/// <summary>Maps between persisted entities and the pure planner contracts.</summary>
public static class RoutinePlanning
{
    private static readonly JsonSerializerOptions SnapshotJson = new() { WriteIndented = false };

    /// <summary>Requires Items → Subcategory → Category to be loaded. Planner "tasks" are subcategories.</summary>
    public static PlanRequest ToPlanRequest(this Routine routine, DateOnly weekStart, int seed)
    {
        ArgumentNullException.ThrowIfNull(routine);

        var tasks = routine.Items
            .Where(i => !i.Subcategory.IsArchived)
            .Select(i => i.Subcategory.ToPlanSpec(i.Weight))
            .ToList();

        var budgets = routine.DayBudgets.ToDictionary(b => b.Day, b => b.Minutes);
        var options = new PlannerOptions(routine.QuantumMinutes, routine.MaxTasksPerDay, routine.PinnedTimeOnTopOfBudget);
        return new PlanRequest(weekStart, budgets, tasks, options, seed);
    }

    public static PlanTaskSpec ToPlanSpec(this Subcategory subcategory, int weight)
    {
        ArgumentNullException.ThrowIfNull(subcategory);
        return new PlanTaskSpec(
            subcategory.Id,
            subcategory.CategoryId,
            subcategory.Category?.SortOrder ?? 0,
            weight,
            subcategory.MinSessionMinutes,
            subcategory.MaxSessionMinutes,
            subcategory.Pin,
            subcategory.PinnedMinutes);
    }

    public static WeeklyPlan ToWeeklyPlan(this PlanResult result, PlanRequest request, int? routineId, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(result);
        var plan = new WeeklyPlan
        {
            WeekStart = result.WeekStart,
            Seed = result.Seed,
            RoutineId = routineId,
            RoutineSnapshotJson = JsonSerializer.Serialize(request, SnapshotJson),
            CreatedAt = createdAt,
        };

        foreach (var d in result.Days)
        {
            var day = new PlanDay
            {
                Date = d.Date,
                BudgetMinutes = d.BudgetMinutes,
                PinnedMinutes = d.PinnedMinutes,
                FlexMinutes = d.FlexMinutes,
            };
            foreach (var e in d.Entries)
                day.Entries.Add(new PlanEntry { SubcategoryId = e.TaskId, Order = e.Order, PlannedMinutes = e.Minutes, IsPinned = e.IsPinned });
            plan.Days.Add(day);
        }

        return plan;
    }

    /// <summary>
    /// Copies a stored week onto another week (or into a saved week), shifting each day by its offset from the
    /// source week start so Monday stays Monday. Progress fields are not copied.
    /// </summary>
    public static WeeklyPlan CloneTo(this WeeklyPlan source, DateOnly weekStart, bool isTemplate, string? name, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(source);
        var plan = new WeeklyPlan
        {
            WeekStart = weekStart,
            Name = name,
            IsTemplate = isTemplate,
            Seed = source.Seed,
            RoutineId = source.RoutineId,
            RoutineSnapshotJson = source.RoutineSnapshotJson,
            CreatedAt = createdAt,
        };

        foreach (var d in source.Days.OrderBy(d => d.Date))
        {
            var offset = d.Date.DayNumber - source.WeekStart.DayNumber;
            var day = new PlanDay
            {
                Date = weekStart.AddDays(offset),
                BudgetMinutes = d.BudgetMinutes,
                PinnedMinutes = d.PinnedMinutes,
                FlexMinutes = d.FlexMinutes,
            };
            foreach (var e in d.Entries.OrderBy(e => e.Order))
                day.Entries.Add(new PlanEntry
                {
                    SubcategoryId = e.SubcategoryId,
                    Order = e.Order,
                    PlannedMinutes = e.PlannedMinutes,
                    IsPinned = e.IsPinned,
                });
            plan.Days.Add(day);
        }

        return plan;
    }

    /// <summary>A stored week as planner days (for <see cref="WeeklyPlanner.Replan"/>). Needs Entries → Subcategory loaded.</summary>
    public static IReadOnlyList<PlannedDay> ToPlannedDays(this WeeklyPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Days.OrderBy(d => d.Date).Select(d => new PlannedDay(
            d.Date, d.BudgetMinutes, d.PinnedMinutes, d.FlexMinutes,
            d.Entries.OrderBy(e => e.Order)
                .Select(e => new PlannedEntry(e.SubcategoryId, e.Subcategory?.CategoryId ?? 0, e.Order, e.PlannedMinutes,
                    e.IsPinned ? PinMode.DailyFirst : PinMode.None))
                .ToList())).ToList();
    }

    public static PlanRequest? ReadSnapshot(this WeeklyPlan plan) =>
        string.IsNullOrEmpty(plan?.RoutineSnapshotJson)
            ? null
            : JsonSerializer.Deserialize<PlanRequest>(plan.RoutineSnapshotJson, SnapshotJson);
}

public static class WeekMath
{
    public static DateOnly StartOfWeek(DateOnly date, DayOfWeek firstDay = DayOfWeek.Monday)
    {
        var diff = ((int)date.DayOfWeek - (int)firstDay + 7) % 7;
        return date.AddDays(-diff);
    }

    /// <summary>
    /// How far to scroll the Monday–Sunday week board (0 = left edge, 1 = right edge) so that the given day is in
    /// view when the app opens: Monday 0, Tuesday 0.2, … Friday 0.8, Saturday and Sunday 1 (user request, 2026-10-18).
    /// </summary>
    public static double BoardScrollFraction(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => 0,
        DayOfWeek.Tuesday => 0.2,
        DayOfWeek.Wednesday => 0.4,
        DayOfWeek.Thursday => 0.6,
        DayOfWeek.Friday => 0.8,
        _ => 1,
    };
}
