using GuitarPractice.Domain.Planning;

namespace GuitarPractice.Domain.Tests;

public class SavedWeekTests
{
    private static readonly DateOnly Monday = Scenarios.Monday;

    private static WeeklyPlan SourceWeek()
    {
        var request = Scenarios.Random(seed: 1234, onTop: false);
        request = request with { WeekStart = Monday };
        return WeeklyPlanner.Generate(request).ToWeeklyPlan(request, routineId: 1, DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void Clone_shifts_every_day_by_whole_weeks_and_keeps_weekdays()
    {
        var source = SourceWeek();
        var target = Monday.AddDays(35);

        var clone = source.CloneTo(target, isTemplate: false, name: "Shred block", DateTimeOffset.UnixEpoch);

        Assert.Equal(target, clone.WeekStart);
        Assert.Equal("Shred block", clone.Name);
        Assert.False(clone.IsTemplate);
        Assert.Equal(7, clone.Days.Count);
        Assert.Equal(source.Days.Select(d => d.Date.DayOfWeek), clone.Days.Select(d => d.Date.DayOfWeek));
        Assert.Equal(source.Days.Select(d => d.Date.AddDays(35)), clone.Days.Select(d => d.Date));
    }

    [Fact]
    public void Clone_copies_minutes_and_entries_but_not_progress()
    {
        var source = SourceWeek();
        source.Days[0].Entries.ForEach(e => { e.ActualSeconds = 99; e.Completed = true; e.BpmAchieved = 140; });

        var clone = source.CloneTo(Monday.AddDays(7), isTemplate: true, name: "x", DateTimeOffset.UnixEpoch);

        Assert.Equal(source.Days.Select(d => d.TotalMinutes), clone.Days.Select(d => d.TotalMinutes));
        Assert.Equal(
            source.Days.SelectMany(d => d.Entries).Select(e => (e.SubcategoryId, e.Order, e.PlannedMinutes, e.IsPinned)),
            clone.Days.SelectMany(d => d.Entries).Select(e => (e.SubcategoryId, e.Order, e.PlannedMinutes, e.IsPinned)));
        Assert.All(clone.Days.SelectMany(d => d.Entries), e =>
        {
            Assert.Null(e.ActualSeconds);
            Assert.False(e.Completed);
        });
        Assert.Equal(source.RoutineSnapshotJson, clone.RoutineSnapshotJson);
    }
}
