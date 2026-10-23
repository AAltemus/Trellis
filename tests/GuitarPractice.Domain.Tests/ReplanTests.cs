using GuitarPractice.Domain.Planning;

namespace GuitarPractice.Domain.Tests;

/// <summary>"Reshuffle from today": <see cref="WeeklyPlanner.Replan"/>.</summary>
public sealed class ReplanTests
{
    private static readonly DateOnly Monday = new(2026, 10, 19);
    private static readonly DateOnly Thursday = Monday.AddDays(3);
    private const int Warmup = 101, Song = 10002, Removed = 777;
    private static readonly int[] Drills = [201, 202, 301, 302, 501, 502];

    private static readonly PlanTaskSpec WarmupSpec = new(Warmup, 1, 0, 1, 5, 10, PinMode.DailyFirst, 5);
    private static readonly PlanTaskSpec SongSpec = new(Song, 9, 85, 2, 15, 30);

    private static PlanRequest Request(bool withSong, int seed = 42)
    {
        var tasks = new List<PlanTaskSpec> { WarmupSpec };
        tasks.AddRange(Drills.Select(id => new PlanTaskSpec(id, id / 100, id / 100 * 10, 2, 10, 20)));
        if (withSong) tasks.Add(SongSpec);
        var budgets = Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => 60);
        return new PlanRequest(Monday, budgets, tasks, new PlannerOptions(PinnedTimeOnTopOfBudget: true), seed);
    }

    /// <summary>The week as it was planned before the song was added.</summary>
    private static PlanResult Before() => WeeklyPlanner.Generate(Request(withSong: false));

    private static int Weekly(PlanResult plan, int task) => plan.WeeklyMinutesFor(task);

    private static int OpenMinutes(PlanResult plan, int task) =>
        plan.Days.Where(d => d.Date >= Thursday).SelectMany(d => d.Entries).Where(e => e.TaskId == task).Sum(e => e.Minutes);

    [Fact]
    public void Days_before_the_first_open_day_are_kept_and_open_days_fill_their_budget()
    {
        var before = Before();
        var after = WeeklyPlanner.Replan(Request(withSong: true, seed: 7), before.Days, Thursday, []);

        for (var d = 0; d < 3; d++) Assert.Same(before.Days[d], after.Days[d]);
        foreach (var day in after.Days.Skip(3))
        {
            Assert.Equal(60, day.FlexMinutes);
            Assert.Equal((Warmup, 5), (day.Entries[0].TaskId, day.Entries[0].Minutes)); // warmup first, as always
            Assert.Equal(day.Entries.Count, day.Entries.Select(e => e.TaskId).Distinct().Count());
        }
    }

    [Fact]
    public void A_task_added_mid_week_catches_up_so_the_whole_week_matches_the_weights()
    {
        var before = Before();
        var after = WeeklyPlanner.Replan(Request(withSong: true, seed: 7), before.Days, Thursday, []);

        // 7 tasks of weight 2 over 420 flexible minutes: 60 each. The song had nothing Mon–Wed, so it gets about
        // 60 of the 240 open minutes; spread evenly by weight alone it would get about 34.
        Assert.InRange(Weekly(after, Song), 50, 70);
        foreach (var drill in Drills) Assert.InRange(Weekly(after, drill), 45, 75);
        Assert.True(OpenMinutes(after, Song) > Drills.Max(d => OpenMinutes(after, d)));
    }

    [Fact]
    public void Kept_items_stay_on_their_day_with_their_minutes_and_never_appear_twice()
    {
        var before = Before();
        var thursdayDrill = before.Days[3].Entries.First(e => !e.IsPinned);
        var after = WeeklyPlanner.Replan(Request(withSong: true, seed: 3), before.Days, Thursday,
            [(Thursday, thursdayDrill.TaskId)]);

        var thursday = after.Days[3];
        var entry = Assert.Single(thursday.Entries, e => e.TaskId == thursdayDrill.TaskId);
        Assert.True(entry.Minutes >= thursdayDrill.Minutes);
        Assert.Equal(60, thursday.FlexMinutes);
    }

    [Fact]
    public void A_kept_item_no_longer_in_the_routine_stays_on_the_board()
    {
        var before = Before();
        var withExtra = before.Days.Select(d => d.Date == Thursday
            ? d with { Entries = [.. d.Entries, new PlannedEntry(Removed, 7, d.Entries.Count, 10, PinMode.None)] }
            : d).ToList();

        var after = WeeklyPlanner.Replan(Request(withSong: true), withExtra, Thursday, [(Thursday, Removed)]);

        Assert.Equal(10, Assert.Single(after.Days[3].Entries, e => e.TaskId == Removed).Minutes);
        Assert.Equal(60, after.Days[3].FlexMinutes); // the rest of Thursday is planned around it
        Assert.DoesNotContain(after.Days[4].Entries, e => e.TaskId == Removed);
    }

    [Fact]
    public void A_big_routine_gets_full_sessions_for_the_items_furthest_behind_instead_of_cut_down_ones()
    {
        // Like the user's routine: 40 items in 420 min a week (about one 10-min session each); mid-week a song and 10
        // more items are added (their week came from an older saved plan), more than the days left can hold.
        var drills = Enumerable.Range(0, 40).Select(n => new PlanTaskSpec(1000 + n, 10 + n / 5, n / 5, 1, 10, 20)).ToList();
        var budgets = Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => 60);
        var added = Enumerable.Range(0, 10).Select(n => new PlanTaskSpec(2000 + n, 20, 20, 1, 10, 20)).Append(SongSpec).ToList();
        PlanRequest Big(bool withAdded) => new(Monday, budgets, [WarmupSpec, .. drills, .. withAdded ? added : []],
            new PlannerOptions(PinnedTimeOnTopOfBudget: true), 5);

        var friday = Thursday.AddDays(1);
        var after = WeeklyPlanner.Replan(Big(withAdded: true), WeeklyPlanner.Generate(Big(withAdded: false)).Days, friday, []);

        var open = after.Days.Where(d => d.Date >= friday).SelectMany(d => d.Entries).Where(e => !e.IsPinned).ToList();
        Assert.All(open, e => Assert.True(e.Minutes >= (e.TaskId == Song ? 15 : 10), $"{e.TaskId}: {e.Minutes} min"));
        Assert.True(open.Where(e => e.TaskId == Song).Sum(e => e.Minutes) >= 15); // the song is furthest behind
        Assert.True(after.HasWarning(PlanWarningCode.InsufficientTimeForMinimums)); // about 27 items behind, 180 min left
        Assert.All(after.Days.Where(d => d.Date >= friday), d => Assert.Equal(60, d.FlexMinutes));
    }

    [Fact]
    public void Same_request_same_plan()
    {
        var before = Before();
        var a = WeeklyPlanner.Replan(Request(withSong: true, seed: 11), before.Days, Thursday, []);
        var b = WeeklyPlanner.Replan(Request(withSong: true, seed: 11), before.Days, Thursday, []);
        Assert.Equal(
            a.Days.SelectMany(d => d.Entries.Select(e => (d.Date, e.TaskId, e.Minutes))),
            b.Days.SelectMany(d => d.Entries.Select(e => (d.Date, e.TaskId, e.Minutes))));
    }

    [Fact]
    public void Opening_the_whole_week_gives_every_task_time_and_fills_every_day()
    {
        var after = WeeklyPlanner.Replan(Request(withSong: true), Before().Days, Monday, []);

        Assert.All(after.Days, d => Assert.Equal(60, d.FlexMinutes));
        foreach (var task in Drills.Append(Song)) Assert.InRange(Weekly(after, task), 50, 70);
    }
}
