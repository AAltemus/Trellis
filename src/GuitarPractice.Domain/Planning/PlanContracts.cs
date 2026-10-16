namespace GuitarPractice.Domain.Planning;

/// <summary>
/// Planner view of a schedulable item. In the app every "task" is a <see cref="Subcategory"/>
/// (TaskId = SubcategoryId). Decoupled from EF entities so the planner stays a pure function.
/// </summary>
public sealed record PlanTaskSpec(
    int TaskId,
    int CategoryId,
    int CategorySortOrder,
    int Weight,
    int MinSessionMinutes,
    int MaxSessionMinutes,
    PinMode Pin = PinMode.None,
    int? PinnedMinutes = null)
{
    public bool IsPinned => Pin != PinMode.None;
    public int EffectivePinnedMinutes => PinnedMinutes ?? MinSessionMinutes;
}

public sealed record PlannerOptions(
    int QuantumMinutes = 5,
    int MaxTasksPerDay = 6,
    bool PinnedTimeOnTopOfBudget = false);

/// <param name="DayBudgets">Minutes per weekday. Missing days are rest days.</param>
public sealed record PlanRequest(
    DateOnly WeekStart,
    IReadOnlyDictionary<DayOfWeek, int> DayBudgets,
    IReadOnlyList<PlanTaskSpec> Tasks,
    PlannerOptions Options,
    int Seed);

public sealed record PlannedEntry(int TaskId, int CategoryId, int Order, int Minutes, PinMode Pin)
{
    public bool IsPinned => Pin != PinMode.None;
}

public sealed record PlannedDay(
    DateOnly Date,
    int BudgetMinutes,
    int PinnedMinutes,
    int FlexMinutes,
    IReadOnlyList<PlannedEntry> Entries)
{
    public int TotalMinutes => PinnedMinutes + FlexMinutes;
    public bool IsRestDay => BudgetMinutes == 0;
}

public enum PlanWarningCode
{
    /// <summary>No task with weight &gt; 0 is selected; flexible minutes are left empty.</summary>
    NoFlexibleTasks,

    /// <summary>The week cannot give every task its MinSessionMinutes; low-weight tasks may be dropped.</summary>
    InsufficientTimeForMinimums,

    /// <summary>Units were moved from high-weight tasks so every task reaches its weekly minimum.</summary>
    MinimumsRebalanced,

    /// <summary>A task needs more sessions than there are active days, so some sessions exceed MaxSessionMinutes.</summary>
    SessionExceedsMax,

    /// <summary>A day ended up with more flexible tasks than MaxTasksPerDay.</summary>
    TooManyTasksOnDay,
}

public sealed record PlanWarning(PlanWarningCode Code, string Message, DateOnly? Date = null, int? TaskId = null);

public sealed record PlanResult(
    DateOnly WeekStart,
    int Seed,
    IReadOnlyList<PlannedDay> Days,
    IReadOnlyList<PlanWarning> Warnings)
{
    public int WeeklyMinutesFor(int taskId) =>
        Days.SelectMany(d => d.Entries).Where(e => e.TaskId == taskId).Sum(e => e.Minutes);

    public bool HasWarning(PlanWarningCode code) => Warnings.Any(w => w.Code == code);
}

public sealed class PlanValidationException(IReadOnlyList<string> errors)
    : Exception("Plan request is invalid: " + string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
