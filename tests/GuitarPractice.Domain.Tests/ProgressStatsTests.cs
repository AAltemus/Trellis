using GuitarPractice.Domain.Practice;

namespace GuitarPractice.Domain.Tests;

public class ProgressStatsTests
{
    private static readonly DateOnly Today = new(2026, 10, 16); // a Friday; week starts Mon Oct 12

    private static ExerciseLog Ex(string name, int? bpm, NoteValue? value = NoteValue.Sixteenths) => new(name, bpm, value);

    private static ProgressDay Day(int daysAgo, int minutes = 0, int? rating = null, string? notes = null, params ExerciseLog[] exercises) =>
        new(Today.AddDays(-daysAgo), minutes * 60, exercises, rating, notes);

    [Fact]
    public void Empty_history_has_no_numbers()
    {
        var s = ProgressStats.Summarize([], 60, 120, Today);
        Assert.Empty(s.Exercises);
        Assert.Null(s.Latest);
        Assert.Equal((null, 0, 0, 0), (s.RecentRating, s.Streak, s.MinutesThisWeek, s.DaysPractised));
    }

    [Fact]
    public void Each_exercise_gets_its_best_latest_and_progress_toward_target()
    {
        var history = new[]
        {
            Day(10, exercises: [Ex("3nps scale", 80)]),
            Day(5, exercises: [Ex("3nps scale", 100), Ex("String skipping", 90, NoteValue.Eighths)]),
            Day(2, exercises: [Ex(" 3NPS  scale ", 95), Ex("String skipping", 96, NoteValue.Eighths)]),
        };

        var s = ProgressStats.Summarize(history, startBpm: 60, targetBpm: 160, Today);

        Assert.Equal(2, s.Exercises.Count);
        var scale = s.Exercises[0];
        Assert.Equal((0, 100, 95, 3), (scale.Slot, scale.BestBpm, scale.LatestBpm, scale.TimesLogged)); // name normalised
        Assert.Equal("3NPS  scale", scale.Name);                    // the latest spelling, trimmed
        Assert.Equal(0.4, scale.TargetFraction!.Value, 6);          // (100 − 60) / (160 − 60)
        Assert.Equal([Today.AddDays(-10), Today.AddDays(-5), Today.AddDays(-2)], scale.BpmHistory.Select(h => h.Date));

        var skipping = s.Exercises[1];
        Assert.Equal((1, 96, NoteValue.Eighths), (skipping.Slot, skipping.BestBpm, skipping.NoteValue));
        Assert.Equal("String skipping · 8ths", skipping.Label);
        Assert.Same(skipping, s.Latest);
    }

    [Fact]
    public void The_same_name_at_another_note_value_is_a_different_exercise()
    {
        var s = ProgressStats.Summarize(
            [Day(3, exercises: [Ex("Scale", 160, NoteValue.Eighths)]), Day(1, exercises: [Ex("scale", 110, NoteValue.Sixteenths)])],
            null, null, Today);

        Assert.Equal(2, s.Exercises.Count);
        Assert.Equal([160, 110], s.Exercises.Select(e => e.BestBpm!.Value));
    }

    [Fact]
    public void Slots_follow_first_logged_order_and_never_change()
    {
        var early = new[] { Day(9, exercises: [Ex("A", 90)]), Day(8, exercises: [Ex("B", 90)]) };
        var later = early.Append(Day(1, exercises: [Ex("C", 100), Ex("A", 120)])).ToArray();

        var before = ProgressStats.Summarize(early, null, null, Today).Exercises.ToDictionary(e => e.Name, e => e.Slot);
        var after = ProgressStats.Summarize(later, null, null, Today).Exercises.ToDictionary(e => e.Name, e => e.Slot);

        Assert.Equal(before["A"], after["A"]);
        Assert.Equal(before["B"], after["B"]);
        Assert.Equal(2, after["C"]);
    }

    [Fact]
    public void Chart_history_keeps_one_point_per_day_the_best()
    {
        var s = ProgressStats.Summarize([Day(1, exercises: [Ex("Lick", 100), Ex("lick", 108)])], null, null, Today);
        Assert.Equal([(Today.AddDays(-1), 108)], s.Exercises[0].BpmHistory);
        Assert.Equal(2, s.Exercises[0].TimesLogged);
    }

    [Fact]
    public void Without_a_start_bpm_the_first_logged_bpm_is_the_baseline_and_fraction_is_clamped()
    {
        Assert.Equal(0.5, ProgressStats.Summarize([Day(9, exercises: [Ex("X", 90)]), Day(1, exercises: [Ex("X", 120)])], null, 150, Today)
            .Exercises[0].TargetFraction!.Value, 6);
        Assert.Equal(1, ProgressStats.Summarize([Day(1, exercises: [Ex("X", 200)])], 60, 160, Today).Exercises[0].TargetFraction);
        Assert.Null(ProgressStats.Summarize([Day(1, exercises: [Ex("X", 100)])], 60, null, Today).Exercises[0].TargetFraction);
    }

    [Fact]
    public void An_exercise_target_overrides_the_block_target()
    {
        var history = new[] { Day(2, exercises: [Ex("Rolling sweeps", 100), Ex("Non-rolling sweeps", 100)]) };
        var own = new Dictionary<string, int> { [ProgressStats.ExerciseKey(" rolling  SWEEPS ", NoteValue.Sixteenths)] = 140 };

        var s = ProgressStats.Summarize(history, startBpm: 60, targetBpm: 180, Today, own);

        var rolling = s.Exercises.Single(e => e.Name == "Rolling sweeps");
        Assert.Equal((140, true), (rolling.TargetBpm, rolling.HasOwnTarget));
        Assert.Equal(0.5, rolling.TargetFraction!.Value, 6);      // (100 − 60) / (140 − 60)
        var other = s.Exercises.Single(e => e.Name == "Non-rolling sweeps");
        Assert.Equal((180, false), (other.TargetBpm, other.HasOwnTarget));
        Assert.Equal(1 / 3.0, other.TargetFraction!.Value, 6);    // (100 − 60) / (180 − 60)
    }

    [Fact]
    public void An_exercise_target_works_without_a_block_target()
    {
        var own = new Dictionary<string, int> { [ProgressStats.ExerciseKey("Lick", NoteValue.Eighths)] = 200 };
        var e = ProgressStats.Summarize([Day(1, exercises: [Ex("Lick", 150, NoteValue.Eighths)])], null, null, Today, own).Exercises.Single();
        Assert.Equal((200, true), (e.TargetBpm, e.HasOwnTarget));
        Assert.NotNull(e.TargetFraction);
    }

    [Fact]
    public void Exercises_without_a_bpm_are_listed_but_have_no_tempo()
    {
        var e = ProgressStats.Summarize([Day(1, exercises: [Ex("Chord changes", null, null)])], 60, 120, Today).Exercises.Single();
        Assert.Equal(("Chord changes", (int?)null, (double?)null, 1), (e.Label, e.BestBpm, e.TargetFraction, e.TimesLogged));
        Assert.Empty(e.BpmHistory);
    }

    [Fact]
    public void Recent_rating_averages_the_last_five_rated_days()
    {
        var history = new[] { Day(9, rating: 1), Day(8, rating: 1), Day(6, rating: 3), Day(5, rating: 4), Day(4, rating: 4), Day(2, rating: 5), Day(1, rating: 4) };
        Assert.Equal(4.0, ProgressStats.Summarize(history, null, null, Today).RecentRating!.Value, 6);
    }

    [Fact]
    public void Streak_counts_consecutive_active_days_ending_today_or_yesterday()
    {
        var history = new[] { Day(1, minutes: 10), Day(2, minutes: 5), Day(3, exercises: [Ex("X", 90)]), Day(4, notes: "felt stiff"), Day(6, minutes: 20) };
        Assert.Equal(4, ProgressStats.Summarize(history, null, null, Today).Streak);
        Assert.Equal(5, ProgressStats.Summarize(history.Append(Day(0, minutes: 15)), null, null, Today).Streak);
        Assert.Equal(0, ProgressStats.Summarize([Day(2, minutes: 10)], null, null, Today).Streak);
    }

    [Fact]
    public void Minutes_this_week_last_28_days_and_days_practised()
    {
        var history = new[] { Day(0, minutes: 10), Day(4, minutes: 20), Day(5, minutes: 30), Day(27, minutes: 40), Day(28, minutes: 50), Day(3, exercises: [Ex("X", 100)]) };
        var s = ProgressStats.Summarize(history, null, null, Today);

        Assert.Equal(30, s.MinutesThisWeek);
        Assert.Equal(100, s.MinutesLast28Days);
        Assert.Equal(5, s.DaysPractised);
        Assert.Equal(Today, s.LastPractised);
    }

    [Fact]
    public void Future_days_are_ignored()
    {
        var s = ProgressStats.Summarize([Day(-3, minutes: 30, exercises: [Ex("X", 200)])], null, null, Today);
        Assert.Empty(s.Exercises);
        Assert.Equal(0, s.MinutesThisWeek);
    }
}
