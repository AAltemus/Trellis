using GuitarPractice.Domain.Planning;

namespace GuitarPractice.Domain.Practice;

/// <summary>One exercise on one day: time from the practice log plus the progress entry, if any.</summary>
public sealed record ProgressDay(DateOnly Date, int SecondsPractised, int? Bpm, int? Rating, string? Notes)
{
    public bool HasActivity => SecondsPractised > 0 || Bpm is not null || Rating is not null || !string.IsNullOrWhiteSpace(Notes);
}

/// <summary>Headline numbers for one exercise.</summary>
/// <param name="TargetFraction">
/// Best BPM's position between the start BPM (or the first logged BPM) and the target, 0..1; null without a target.
/// </param>
/// <param name="Streak">Consecutive days with practice or a log entry, ending today (or yesterday, so it isn't
/// "broken" before you've practised today).</param>
public sealed record ProgressSummary(
    int? BestBpm,
    int? LatestBpm,
    DateOnly? LatestBpmDate,
    double? TargetFraction,
    double? RecentRating,
    int Streak,
    int MinutesThisWeek,
    int MinutesLast28Days,
    DateOnly? LastPractised,
    int DaysPractised);

public static class ProgressStats
{
    /// <summary>How many of the latest rated days <see cref="ProgressSummary.RecentRating"/> averages.</summary>
    public const int RecentRatings = 5;

    public static ProgressSummary Summarize(IEnumerable<ProgressDay> history, int? startBpm, int? targetBpm, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(history);
        var days = history.Where(d => d.Date <= today).OrderBy(d => d.Date).ToList();

        var withBpm = days.Where(d => d.Bpm is not null).ToList();
        int? best = withBpm.Count > 0 ? withBpm.Max(d => d.Bpm!.Value) : null;
        var latest = withBpm.Count > 0 ? withBpm[^1] : null;

        double? fraction = null;
        if (best is int b && targetBpm is int target)
        {
            var from = startBpm ?? withBpm[0].Bpm!.Value;
            fraction = target > from ? Math.Clamp((b - from) / (double)(target - from), 0, 1) : (b >= target ? 1 : 0);
        }

        var rated = days.Where(d => d.Rating is not null).TakeLast(RecentRatings).ToList();
        double? recentRating = rated.Count > 0 ? rated.Average(d => d.Rating!.Value) : null;

        var active = days.Where(d => d.HasActivity).Select(d => d.Date).ToHashSet();
        var cursor = active.Contains(today) ? today : today.AddDays(-1);
        var streak = 0;
        while (active.Contains(cursor))
        {
            streak++;
            cursor = cursor.AddDays(-1);
        }

        var weekStart = WeekMath.StartOfWeek(today);
        var minutesThisWeek = days.Where(d => d.Date >= weekStart).Sum(d => d.SecondsPractised) / 60;
        var minutes28 = days.Where(d => d.Date > today.AddDays(-28)).Sum(d => d.SecondsPractised) / 60;
        var practised = days.Where(d => d.SecondsPractised > 0).ToList();

        return new ProgressSummary(
            best,
            latest?.Bpm,
            latest?.Date,
            fraction,
            recentRating,
            streak,
            minutesThisWeek,
            minutes28,
            practised.Count > 0 ? practised[^1].Date : null,
            practised.Count);
    }
}
