namespace GuitarPractice.Domain;

/// <summary>
/// A stored week. Two kinds share this table:
///  - Locked week (IsTemplate = false): the frozen plan for <see cref="WeekStart"/>; at most one per week.
///    While it exists the week doesn't regenerate from routine changes.
///  - Saved week (IsTemplate = true): a named copy you can load into any later week. Its WeekStart is the week it
///    was saved from; loading shifts its days by weekday onto the target week.
/// </summary>
public class WeeklyPlan
{
    public int Id { get; set; }
    public DateOnly WeekStart { get; set; }

    /// <summary>Saved weeks: the user's name. Locked weeks: the saved week it was loaded from, if any.</summary>
    public string? Name { get; set; }

    public bool IsTemplate { get; set; }

    /// <summary>Planner seed: same routine snapshot + seed regenerates the identical plan.</summary>
    public int Seed { get; set; }

    /// <summary>Nullable so deleting a routine never deletes practice history.</summary>
    public int? RoutineId { get; set; }
    public Routine? Routine { get; set; }

    /// <summary>The exact PlanRequest (weights, budgets, budget mode) used to generate this plan.</summary>
    public string RoutineSnapshotJson { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public List<PlanDay> Days { get; } = [];
}

public class PlanDay
{
    public int Id { get; set; }
    public int WeeklyPlanId { get; set; }
    public WeeklyPlan WeeklyPlan { get; set; } = null!;
    public DateOnly Date { get; set; }

    /// <summary>What the user entered for this weekday.</summary>
    public int BudgetMinutes { get; set; }

    /// <summary>Sum of pinned entries (warmup etc.).</summary>
    public int PinnedMinutes { get; set; }

    /// <summary>Minutes distributed by the weighted allocator.</summary>
    public int FlexMinutes { get; set; }

    /// <summary>Not mapped. Equals BudgetMinutes in "in budget" mode, Budget + Pinned in "on top" mode.</summary>
    public int TotalMinutes => PinnedMinutes + FlexMinutes;

    public List<PlanEntry> Entries { get; } = [];
}

public class PlanEntry
{
    public int Id { get; set; }
    public int PlanDayId { get; set; }
    public PlanDay PlanDay { get; set; } = null!;
    public int SubcategoryId { get; set; }
    public Subcategory Subcategory { get; set; } = null!;

    public int Order { get; set; }
    public int PlannedMinutes { get; set; }
    public bool IsPinned { get; set; }

    // Filled in by the session runner (M3).
    public int? ActualSeconds { get; set; }
    public int? BpmAchieved { get; set; }
    public bool Completed { get; set; }
}
