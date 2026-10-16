using GuitarPractice.Domain;
using GuitarPractice.Domain.Planning;

namespace GuitarPractice.Domain.Tests;

/// <summary>
/// Deterministic random routine generator. FsCheck drives the seed and the mode flags; this turns them
/// into a realistic PlanRequest, so a failing case replays exactly from the seed FsCheck reports.
/// </summary>
internal static class Scenarios
{
    public static readonly DateOnly Monday = new(2026, 10, 19);

    public static PlanRequest Random(int seed, bool onTop, bool quantizedBudgets = true)
    {
        var rng = new Random(seed);
        int[] quanta = [5, 5, 5, 10, 15];
        var q = quanta[rng.Next(quanta.Length)];

        var taskCount = rng.Next(1, 16);
        var tasks = new List<PlanTaskSpec>(taskCount);
        for (var i = 0; i < taskCount; i++)
        {
            var category = rng.Next(1, 9);
            var min = q * rng.Next(1, 3);
            var max = min + q * rng.Next(0, 4);
            var weight = rng.Next(0, 6) == 0 ? 0 : rng.Next(1, 11);   // ~1 in 6 selected-but-zero
            tasks.Add(new PlanTaskSpec(100 + i, category, category * 10, weight, min, max));
        }

        if (rng.Next(4) == 0)
            tasks[0] = tasks[0] with { Pin = PinMode.DailyFirst, PinnedMinutes = q * rng.Next(1, 3) };
        if (taskCount > 1 && rng.Next(6) == 0)
            tasks[^1] = tasks[^1] with { Pin = PinMode.DailyLast, PinnedMinutes = rng.Next(0, 2) == 0 ? null : q };

        var pinnedTotal = tasks.Where(t => t.IsPinned).Sum(t => t.EffectivePinnedMinutes);

        var budgets = new Dictionary<DayOfWeek, int>();
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            if (rng.Next(5) == 0) { budgets[day] = 0; continue; }   // rest day
            var minutes = quantizedBudgets ? q * rng.Next(1, 25) : rng.Next(1, 181);
            if (!onTop) minutes += pinnedTotal;                      // keep "in budget" requests valid
            budgets[day] = minutes;
        }

        var options = new PlannerOptions(q, rng.Next(2, 9), onTop);
        var weekStart = Monday.AddDays(7 * rng.Next(0, 52));
        return new PlanRequest(weekStart, budgets, tasks, options, seed);
    }

    /// <summary>A stable text fingerprint of a plan, for determinism checks.</summary>
    public static string Fingerprint(PlanResult plan) =>
        string.Join("|", plan.Days.Select(d =>
            $"{d.Date:yyyy-MM-dd}:{d.PinnedMinutes}+{d.FlexMinutes}=" +
            string.Join(",", d.Entries.Select(e => $"{e.TaskId}@{e.Order}x{e.Minutes}"))));
}
