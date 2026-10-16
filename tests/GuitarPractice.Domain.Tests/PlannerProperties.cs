using FsCheck.Xunit;
using GuitarPractice.Domain;
using GuitarPractice.Domain.Planning;

namespace GuitarPractice.Domain.Tests;

/// <summary>
/// Planner invariants, checked against randomly generated routines in both budget modes.
/// FsCheck supplies (seed, salt, onTop); <see cref="Scenarios.Random"/> expands that into a full request.
/// </summary>
public class PlannerProperties
{
    private const int Runs = 500;

    private static int Seed(int seed, int salt) => unchecked(seed * 7919 + salt);

    [Property(MaxTest = Runs)]
    public void Each_day_total_matches_budget_mode(int seed, int salt, bool onTop, bool quantized)
    {
        var request = Scenarios.Random(Seed(seed, salt), onTop, quantized);
        var plan = WeeklyPlanner.Generate(request);
        var hasFlex = request.Tasks.Any(t => !t.IsPinned && t.Weight > 0);

        foreach (var day in plan.Days)
        {
            if (day.BudgetMinutes == 0)
            {
                Assert.Empty(day.Entries);
                continue;
            }

            var pinned = request.Tasks.Where(t => t.IsPinned).Sum(t => t.EffectivePinnedMinutes);
            Assert.Equal(pinned, day.PinnedMinutes);

            if (!hasFlex) continue;
            Assert.Equal(onTop ? day.BudgetMinutes : day.BudgetMinutes - day.PinnedMinutes, day.FlexMinutes);
            Assert.Equal(onTop ? day.BudgetMinutes + day.PinnedMinutes : day.BudgetMinutes, day.TotalMinutes);
        }
    }

    [Property(MaxTest = Runs)]
    public void Weekly_totals_are_within_one_quantum_of_weighted_ideal(int seed, int salt, bool onTop)
    {
        var request = Scenarios.Random(Seed(seed, salt), onTop, quantizedBudgets: true);
        var plan = WeeklyPlanner.Generate(request);
        if (plan.HasWarning(PlanWarningCode.MinimumsRebalanced) ||
            plan.HasWarning(PlanWarningCode.InsufficientTimeForMinimums))
            return; // minimum guarantees intentionally override proportionality

        var flex = request.Tasks.Where(t => !t.IsPinned && t.Weight > 0).ToList();
        var flexWeek = plan.Days.Sum(d => d.FlexMinutes);
        double weightSum = flex.Sum(t => t.Weight);

        foreach (var t in flex)
        {
            var ideal = flexWeek * t.Weight / weightSum;
            var actual = plan.WeeklyMinutesFor(t.TaskId);
            Assert.True(Math.Abs(actual - ideal) < request.Options.QuantumMinutes,
                $"Task {t.TaskId}: {actual} min vs ideal {ideal:F2} (q={request.Options.QuantumMinutes})");
        }
    }

    [Property(MaxTest = Runs)]
    public void No_task_appears_twice_on_the_same_day(int seed, int salt, bool onTop, bool quantized)
    {
        var plan = WeeklyPlanner.Generate(Scenarios.Random(Seed(seed, salt), onTop, quantized));
        foreach (var day in plan.Days)
            Assert.Equal(day.Entries.Count, day.Entries.Select(e => e.TaskId).Distinct().Count());
    }

    [Property(MaxTest = Runs)]
    public void Zero_weight_tasks_never_appear(int seed, int salt, bool onTop)
    {
        var request = Scenarios.Random(Seed(seed, salt), onTop);
        var zero = request.Tasks.Where(t => !t.IsPinned && t.Weight == 0).Select(t => t.TaskId).ToHashSet();
        var plan = WeeklyPlanner.Generate(request);
        Assert.DoesNotContain(plan.Days.SelectMany(d => d.Entries), e => zero.Contains(e.TaskId));
    }

    [Property(MaxTest = Runs)]
    public void Same_seed_produces_identical_plan(int seed, int salt, bool onTop, bool quantized)
    {
        var request = Scenarios.Random(Seed(seed, salt), onTop, quantized);
        Assert.Equal(
            Scenarios.Fingerprint(WeeklyPlanner.Generate(request)),
            Scenarios.Fingerprint(WeeklyPlanner.Generate(request)));
    }

    [Property(MaxTest = Runs)]
    public void Pinned_first_lead_and_pinned_last_close_every_practice_day(int seed, int salt, bool onTop)
    {
        var plan = WeeklyPlanner.Generate(Scenarios.Random(Seed(seed, salt), onTop));
        foreach (var day in plan.Days.Where(d => d.Entries.Count > 0))
        {
            var pins = day.Entries.Select(e => e.Pin).ToList();
            var firstFlex = pins.FindIndex(p => p != PinMode.DailyFirst);
            if (firstFlex >= 0)
                Assert.DoesNotContain(pins.Skip(firstFlex), p => p == PinMode.DailyFirst);
            var lastBeforeTail = pins.FindLastIndex(p => p != PinMode.DailyLast);
            Assert.DoesNotContain(pins.Take(lastBeforeTail + 1), p => p == PinMode.DailyLast);
            Assert.Equal(Enumerable.Range(0, day.Entries.Count), day.Entries.Select(e => e.Order));
        }
    }

    [Property(MaxTest = Runs)]
    public void Flexible_sessions_are_whole_quanta_except_leftover_minutes(int seed, int salt, bool onTop)
    {
        var request = Scenarios.Random(Seed(seed, salt), onTop, quantizedBudgets: true);
        var plan = WeeklyPlanner.Generate(request);
        Assert.All(plan.Days.SelectMany(d => d.Entries).Where(e => !e.IsPinned),
            e => Assert.True(e.Minutes > 0 && e.Minutes % request.Options.QuantumMinutes == 0,
                $"Entry for task {e.TaskId} has {e.Minutes} min (q={request.Options.QuantumMinutes})"));
    }
}
