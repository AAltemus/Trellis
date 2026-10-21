using GuitarPractice.Domain.Planning;

namespace GuitarPractice.Domain.Tests;

public sealed class PlanSwapTests
{
    private static readonly DateOnly Monday = new(2026, 10, 19);
    private static readonly DateOnly Tuesday = Monday.AddDays(1);
    private const int Warmup = 101, Theory = 1101, Legato = 201, Sweep = 501;

    /// <summary>Mon: warmup 5, theory 10, sweep 20 · Tue: warmup 5, legato 15, sweep 10.</summary>
    private static WeeklyPlan Week()
    {
        var plan = new WeeklyPlan { WeekStart = Monday };
        plan.Days.Add(Day(Monday, (Warmup, 5, true), (Theory, 10, false), (Sweep, 20, false)));
        plan.Days.Add(Day(Tuesday, (Warmup, 5, true), (Legato, 15, false), (Sweep, 10, false)));
        return plan;
    }

    private static PlanDay Day(DateOnly date, params (int Sub, int Minutes, bool Pinned)[] entries)
    {
        var day = new PlanDay { Date = date };
        for (var i = 0; i < entries.Length; i++)
            day.Entries.Add(new PlanEntry { SubcategoryId = entries[i].Sub, PlannedMinutes = entries[i].Minutes, Order = i, IsPinned = entries[i].Pinned });
        return day;
    }

    private static List<(int Sub, int Minutes, int Order)> Shape(WeeklyPlan plan, DateOnly date) =>
        [.. plan.Days.Single(d => d.Date == date).Entries.Select(e => (e.SubcategoryId, e.PlannedMinutes, e.Order))];

    [Fact]
    public void Exercises_trade_places_and_each_slot_keeps_its_minutes()
    {
        var plan = Week();
        PlanSwap.Apply(plan, (Monday, Theory), (Tuesday, Legato));

        // The user's example: 10 min Theory (Mon) ↔ 15 min Legato (Tue) → 10 min Legato (Mon), 15 min Theory (Tue).
        Assert.Equal([(Warmup, 5, 0), (Legato, 10, 1), (Sweep, 20, 2)], Shape(plan, Monday));
        Assert.Equal([(Warmup, 5, 0), (Theory, 15, 1), (Sweep, 10, 2)], Shape(plan, Tuesday));
        Assert.Equal([35, 30], plan.Days.Select(d => d.Entries.Sum(e => e.PlannedMinutes))); // day totals unchanged
    }

    [Fact]
    public void Two_items_on_the_same_day_trade_minutes_and_positions()
    {
        var plan = Week();
        PlanSwap.Apply(plan, (Monday, Sweep), (Monday, Theory));
        Assert.Equal([(Warmup, 5, 0), (Sweep, 10, 1), (Theory, 20, 2)], Shape(plan, Monday));
    }

    [Fact]
    public void Warmups_duplicates_and_unknown_items_are_refused_and_nothing_changes()
    {
        var plan = Week();
        Assert.Equal(SwapRefusal.Pinned, PlanSwap.Check(plan, (Monday, Warmup), (Tuesday, Legato)));
        Assert.Equal(SwapRefusal.Pinned, PlanSwap.Check(plan, (Monday, Theory), (Tuesday, Warmup)));
        // Sweep is on both days: moving either sweep onto the other day would put it there twice.
        Assert.Equal(SwapRefusal.AlreadyOnThatDay, PlanSwap.Check(plan, (Monday, Sweep), (Tuesday, Legato)));
        Assert.Equal(SwapRefusal.SameItem, PlanSwap.Check(plan, (Monday, Theory), (Monday, Theory)));
        Assert.Equal(SwapRefusal.NotFound, PlanSwap.Check(plan, (Monday, Legato), (Tuesday, Sweep)));
        Assert.Equal(SwapRefusal.AlreadyOnThatDay, PlanSwap.Check(plan, (Monday, Theory), (Tuesday, Sweep)));
        Assert.Equal(SwapRefusal.None, PlanSwap.Check(plan, (Monday, Theory), (Tuesday, Legato)));

        Assert.Throws<InvalidOperationException>(() => PlanSwap.Apply(plan, (Monday, Sweep), (Tuesday, Legato)));
        Assert.Equal([(Warmup, 5, 0), (Theory, 10, 1), (Sweep, 20, 2)], Shape(plan, Monday));
        Assert.Equal([(Warmup, 5, 0), (Legato, 15, 1), (Sweep, 10, 2)], Shape(plan, Tuesday));
    }
}
