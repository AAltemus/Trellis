using GuitarPractice.Domain;
using GuitarPractice.Domain.Planning;

namespace GuitarPractice.Domain.Tests;

public class PlannerExampleTests
{
    private static readonly DateOnly Monday = Scenarios.Monday;

    private static Dictionary<DayOfWeek, int> EveryDay(int minutes) =>
        Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => minutes);

    private static readonly PlanTaskSpec Warmup =
        new(1, CategoryId: 1, CategorySortOrder: 0, Weight: 0, 5, 10, PinMode.DailyFirst, PinnedMinutes: 10);

    private static PlanTaskSpec Task(int id, int sortOrder, int weight, int min = 5, int max = 20) =>
        new(id, CategoryId: sortOrder, CategorySortOrder: sortOrder, weight, min, max);

    [Fact]
    public void Hamilton_allocation_is_exact_for_divisible_weights()
    {
        var units = WeeklyPlanner.AllocateUnits([1, 2, 3], 12, [0, 0, 0]);
        Assert.Equal(new[] { 2, 4, 6 }, units);
    }

    [Fact]
    public void Hamilton_allocation_gives_leftovers_to_largest_remainders()
    {
        // ideals: 10*1/3=3.33, 10*1/3=3.33, 10*1/3=3.33 -> one extra unit, tie broken by tieBreak key
        var units = WeeklyPlanner.AllocateUnits([1, 1, 1], 10, [5, 1, 9]);
        Assert.Equal(new[] { 3, 4, 3 }, units);
    }

    [Fact]
    public void In_budget_mode_warmup_is_carved_out_of_the_day()
    {
        var request = new PlanRequest(Monday, EveryDay(60), [Warmup, Task(10, 10, 5), Task(20, 20, 5)],
            new PlannerOptions(PinnedTimeOnTopOfBudget: false), Seed: 1);

        var plan = WeeklyPlanner.Generate(request);

        Assert.All(plan.Days, d =>
        {
            Assert.Equal(60, d.TotalMinutes);
            Assert.Equal(10, d.PinnedMinutes);
            Assert.Equal(50, d.FlexMinutes);
            Assert.Equal(1, d.Entries[0].TaskId);
        });
    }

    [Fact]
    public void On_top_mode_adds_warmup_to_the_day()
    {
        var request = new PlanRequest(Monday, EveryDay(60), [Warmup, Task(10, 10, 5), Task(20, 20, 5)],
            new PlannerOptions(PinnedTimeOnTopOfBudget: true), Seed: 1);

        var plan = WeeklyPlanner.Generate(request);

        Assert.All(plan.Days, d =>
        {
            Assert.Equal(70, d.TotalMinutes);
            Assert.Equal(60, d.FlexMinutes);
        });
    }

    [Fact]
    public void Rest_day_gets_no_warmup_in_either_mode()
    {
        foreach (var onTop in new[] { false, true })
        {
            var budgets = EveryDay(45);
            budgets[DayOfWeek.Sunday] = 0;
            var request = new PlanRequest(Monday, budgets, [Warmup, Task(10, 10, 5)],
                new PlannerOptions(PinnedTimeOnTopOfBudget: onTop), Seed: 3);

            var sunday = WeeklyPlanner.Generate(request).Days.Single(d => d.Date.DayOfWeek == DayOfWeek.Sunday);

            Assert.Empty(sunday.Entries);
            Assert.Equal(0, sunday.TotalMinutes);
        }
    }

    [Fact]
    public void Pinned_time_exceeding_budget_fails_validation_only_in_budget_mode()
    {
        var budgets = EveryDay(30);
        budgets[DayOfWeek.Tuesday] = 5;
        var tasks = new[] { Warmup, Task(10, 10, 5) };

        var inBudget = new PlanRequest(Monday, budgets, tasks, new PlannerOptions(PinnedTimeOnTopOfBudget: false), 1);
        var ex = Assert.Throws<PlanValidationException>(() => WeeklyPlanner.Generate(inBudget));
        Assert.Contains(ex.Errors, e => e.Contains("Tuesday", StringComparison.Ordinal));

        var onTop = inBudget with { Options = new PlannerOptions(PinnedTimeOnTopOfBudget: true) };
        var tuesday = WeeklyPlanner.Generate(onTop).Days.Single(d => d.Date.DayOfWeek == DayOfWeek.Tuesday);
        Assert.Equal(15, tuesday.TotalMinutes);
    }

    [Fact]
    public void Flexible_tasks_are_ordered_by_category_sort_order()
    {
        var request = new PlanRequest(Monday, EveryDay(90),
            [Task(30, sortOrder: 90, 5), Task(10, sortOrder: 10, 5), Task(20, sortOrder: 50, 5)],
            new PlannerOptions(MaxTasksPerDay: 3), Seed: 11);

        foreach (var day in WeeklyPlanner.Generate(request).Days)
        {
            var sortOrders = day.Entries.Select(e => e.CategoryId).ToList();
            Assert.Equal(sortOrders.Order().ToList(), sortOrders);
        }
    }

    [Fact]
    public void Sessions_are_spread_across_the_week_instead_of_massed()
    {
        // 8 equal tasks, 60 min/day: each gets ~52 min/week, max 20 min/session -> 3 sessions.
        // Spacing should avoid putting all three on consecutive days.
        var tasks = Enumerable.Range(0, 8).Select(i => Task(10 + i, 10 * (i + 1), 5)).ToList();
        var request = new PlanRequest(Monday, EveryDay(60), tasks, new PlannerOptions(MaxTasksPerDay: 4), Seed: 42);

        var plan = WeeklyPlanner.Generate(request);

        foreach (var t in tasks)
        {
            var dayIdx = plan.Days.Select((d, i) => (d, i))
                .Where(x => x.d.Entries.Any(e => e.TaskId == t.TaskId))
                .Select(x => x.i)
                .ToList();
            Assert.True(dayIdx.Count >= 2, $"Task {t.TaskId} only on {dayIdx.Count} day(s)");
            var threeInARow = dayIdx.Any(i => dayIdx.Contains(i + 1) && dayIdx.Contains(i + 2));
            Assert.False(threeInARow, $"Task {t.TaskId} is massed on consecutive days: {string.Join(",", dayIdx)}");
        }
    }

    [Fact]
    public void Realistic_week_respects_session_sizes_and_spacing()
    {
        // All ten built-in technique/creative categories, 60 min weekdays + 30 min Sunday, warmup carved out.
        int[] weights = [8, 3, 5, 6, 4, 7, 2, 3, 5, 4];
        var tasks = new List<PlanTaskSpec> { Warmup };
        tasks.AddRange(weights.Select((w, i) => Task(10 + i, 10 * (i + 1), w, min: 10, max: 20)));
        var budgets = EveryDay(60);
        budgets[DayOfWeek.Sunday] = 30;

        var plan = WeeklyPlanner.Generate(new PlanRequest(Monday, budgets, tasks, new PlannerOptions(), Seed: 7));

        Assert.Empty(plan.Warnings);
        Assert.All(plan.Days.SelectMany(d => d.Entries).Where(e => !e.IsPinned),
            e => Assert.True(e.Minutes is >= 10 and <= 20, $"Task {e.TaskId}: {e.Minutes} min"));
        foreach (var t in tasks.Skip(1))
        {
            var dayIdx = plan.Days.Select((d, i) => (d, i))
                .Where(x => x.d.Entries.Any(e => e.TaskId == t.TaskId)).Select(x => x.i).ToList();
            Assert.False(dayIdx.Zip(dayIdx.Skip(1)).Any(p => p.Second == p.First + 1),
                $"Task {t.TaskId} on consecutive days: {string.Join(",", dayIdx)}");
        }
    }

    [Fact]
    public void Too_little_time_for_minimums_warns()
    {
        var tasks = Enumerable.Range(0, 10).Select(i => Task(10 + i, 10, 1, min: 15, max: 20)).ToList();
        var budgets = new Dictionary<DayOfWeek, int> { [DayOfWeek.Monday] = 20, [DayOfWeek.Thursday] = 20 };
        var plan = WeeklyPlanner.Generate(new PlanRequest(Monday, budgets, tasks, new PlannerOptions(), 5));

        Assert.True(plan.HasWarning(PlanWarningCode.InsufficientTimeForMinimums));
        Assert.Equal(40, plan.Days.Sum(d => d.TotalMinutes));
    }

    [Fact]
    public void Minimums_rebalance_moves_time_from_heavy_tasks()
    {
        // 35 min/week; light task ideal = 35 * 1/11 ≈ 3 min, but its minimum is 10.
        var tasks = new[] { Task(10, 10, 10, min: 5, max: 30), Task(20, 20, 1, min: 10, max: 30) };
        var budgets = new Dictionary<DayOfWeek, int> { [DayOfWeek.Monday] = 35 };
        var plan = WeeklyPlanner.Generate(new PlanRequest(Monday, budgets, tasks, new PlannerOptions(), 2));

        Assert.True(plan.HasWarning(PlanWarningCode.MinimumsRebalanced));
        Assert.Equal(10, plan.WeeklyMinutesFor(20));
        Assert.Equal(25, plan.WeeklyMinutesFor(10));
    }

    [Fact]
    public void Odd_budget_minutes_are_still_fully_used()
    {
        var request = new PlanRequest(Monday, EveryDay(47), [Task(10, 10, 3), Task(20, 20, 7)],
            new PlannerOptions(QuantumMinutes: 5), Seed: 9);
        Assert.All(WeeklyPlanner.Generate(request).Days, d => Assert.Equal(47, d.TotalMinutes));
    }

    [Fact]
    public void Routine_round_trips_through_entities()
    {
        var category = new Category { Id = 3, Name = "Legato", SortOrder = 40 };
        var sub = new Subcategory { Id = 7, Name = "Trills", Category = category, CategoryId = 3 };
        var archived = new Subcategory { Id = 8, Name = "Old drill", Category = category, CategoryId = 3, IsArchived = true };
        var routine = new Routine { Id = 1, Name = "Test", PinnedTimeOnTopOfBudget = true };
        routine.Items.Add(new RoutineItem { SubcategoryId = 7, Subcategory = sub, Weight = 4 });
        routine.Items.Add(new RoutineItem { SubcategoryId = 8, Subcategory = archived, Weight = 9 });
        routine.DayBudgets.Add(new RoutineDayBudget { Day = DayOfWeek.Monday, Minutes = 30 });

        var request = routine.ToPlanRequest(Monday, seed: 123);
        var entity = WeeklyPlanner.Generate(request).ToWeeklyPlan(request, routine.Id, DateTimeOffset.UnixEpoch);

        Assert.Equal(7, entity.Days.Count);
        Assert.Equal(30, entity.Days[0].TotalMinutes);
        var snapshot = entity.ReadSnapshot()!;
        Assert.True(snapshot.Options.PinnedTimeOnTopOfBudget);
        Assert.Equal(40, snapshot.Tasks.Single().CategorySortOrder);   // archived item excluded
        Assert.All(entity.Days.SelectMany(d => d.Entries), e => Assert.Equal(7, e.SubcategoryId));
    }

    [Fact]
    public void Start_of_week_handles_every_weekday()
    {
        for (var i = 0; i < 7; i++)
            Assert.Equal(Monday, WeekMath.StartOfWeek(Monday.AddDays(i)));
        Assert.Equal(Monday.AddDays(-1), WeekMath.StartOfWeek(Monday, DayOfWeek.Sunday));
    }
}
