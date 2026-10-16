namespace GuitarPractice.Domain;

/// <summary>A saved selection of subcategories + weights + daily budgets ("Shred block", "Writing week").</summary>
public class Routine
{
    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>Allocation granularity in minutes. Every flexible session is a multiple of this.</summary>
    public int QuantumMinutes { get; set; } = 5;

    /// <summary>Soft cap on flexible (non-pinned) subcategories per day.</summary>
    public int MaxTasksPerDay { get; set; } = 6;

    /// <summary>
    /// false: pinned tasks (warmup) are carved out of each day's budget.
    /// true:  pinned time is added on top; the whole budget is weighted practice.
    /// </summary>
    public bool PinnedTimeOnTopOfBudget { get; set; }

    /// <summary>Planner seed for the live week. Saved so the week doesn't reshuffle between app launches.</summary>
    public int PlanSeed { get; set; } = 1;

    public List<RoutineItem> Items { get; } = [];
    public List<RoutineDayBudget> DayBudgets { get; } = [];
}

public class RoutineItem
{
    public int RoutineId { get; set; }
    public Routine Routine { get; set; } = null!;
    public int SubcategoryId { get; set; }
    public Subcategory Subcategory { get; set; } = null!;

    /// <summary>1–10 in the UI. Weight 0 removes the item from the routine. Pinned items store 1 (weight is ignored).</summary>
    public int Weight { get; set; } = 5;
}

public class RoutineDayBudget
{
    public int RoutineId { get; set; }
    public Routine Routine { get; set; } = null!;
    public DayOfWeek Day { get; set; }

    /// <summary>0 = rest day (no warmup either, in both budget modes).</summary>
    public int Minutes { get; set; }
}
