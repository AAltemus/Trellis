using GuitarPractice.Domain.Practice;

namespace GuitarPractice.Domain.Tests;

public class ProgressStatsTests
{
    private static readonly DateOnly Today = new(2026, 10, 16); // a Friday; week starts Mon Oct 12

    private static ProgressDay Day(int daysAgo, int minutes = 0, int? bpm = null, int? rating = null, string? notes = null) =>
        new(Today.AddDays(-daysAgo), minutes * 60, bpm, rating, notes);

    [Fact]
    public void Empty_history_has_no_numbers()
    {
        var s = ProgressStats.Summarize([], 60, 120, Today);
        Assert.Equal((null, null, null, null, 0, 0, 0), (s.BestBpm, s.LatestBpm, s.TargetFraction, s.RecentRating, s.Streak, s.MinutesThisWeek, s.DaysPractised));
        Assert.Null(s.LastPractised);
    }

    [Fact]
    public void Best_and_latest_bpm_and_progress_toward_target()
    {
        var s = ProgressStats.Summarize([Day(10, bpm: 80), Day(5, bpm: 100), Day(2, bpm: 95)], startBpm: 60, targetBpm: 160, Today);

        Assert.Equal(100, s.BestBpm);
        Assert.Equal(95, s.LatestBpm);
        Assert.Equal(Today.AddDays(-2), s.LatestBpmDate);
        Assert.Equal(0.4, s.TargetFraction!.Value, 6); // (100 − 60) / (160 − 60)
    }

    [Fact]
    public void Without_a_start_bpm_the_first_logged_bpm_is_the_baseline()
    {
        var s = ProgressStats.Summarize([Day(9, bpm: 90), Day(1, bpm: 120)], startBpm: null, targetBpm: 150, Today);
        Assert.Equal(0.5, s.TargetFraction!.Value, 6);
    }

    [Fact]
    public void Target_fraction_is_clamped_and_absent_without_a_target()
    {
        Assert.Equal(1, ProgressStats.Summarize([Day(1, bpm: 200)], 60, 160, Today).TargetFraction);
        Assert.Equal(0, ProgressStats.Summarize([Day(1, bpm: 40)], 60, 160, Today).TargetFraction);
        Assert.Null(ProgressStats.Summarize([Day(1, bpm: 100)], 60, null, Today).TargetFraction);
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
        // Practised 1, 2, 3 days ago, plus a notes-only entry 4 days ago; nothing today yet; gap at 5 days ago.
        var history = new[] { Day(1, minutes: 10), Day(2, minutes: 5), Day(3, bpm: 90), Day(4, notes: "felt stiff"), Day(6, minutes: 20) };
        Assert.Equal(4, ProgressStats.Summarize(history, null, null, Today).Streak);

        var withToday = history.Append(Day(0, minutes: 15));
        Assert.Equal(5, ProgressStats.Summarize(withToday, null, null, Today).Streak);

        Assert.Equal(0, ProgressStats.Summarize([Day(2, minutes: 10)], null, null, Today).Streak); // broken yesterday
    }

    [Fact]
    public void Minutes_this_week_last_28_days_and_days_practised()
    {
        var history = new[] { Day(0, minutes: 10), Day(4, minutes: 20), Day(5, minutes: 30), Day(27, minutes: 40), Day(28, minutes: 50), Day(3, bpm: 100) };
        var s = ProgressStats.Summarize(history, null, null, Today);

        Assert.Equal(30, s.MinutesThisWeek);     // Fri 10 + Mon 20; Sun (5 days ago) is last week
        Assert.Equal(100, s.MinutesLast28Days);  // 10 + 20 + 30 + 40 (28 days ago is outside)
        Assert.Equal(5, s.DaysPractised);        // the BPM-only day had no time
        Assert.Equal(Today, s.LastPractised);
    }

    [Fact]
    public void Future_days_are_ignored()
    {
        var s = ProgressStats.Summarize([Day(-3, minutes: 30, bpm: 200)], null, null, Today);
        Assert.Null(s.BestBpm);
        Assert.Equal(0, s.MinutesThisWeek);
    }
}
