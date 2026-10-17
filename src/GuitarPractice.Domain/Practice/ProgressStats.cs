using System.Globalization;
using GuitarPractice.Domain.Planning;

namespace GuitarPractice.Domain.Practice;

/// <summary>One exercise line as logged: name, tempo and the note value the tempo counts.</summary>
public sealed record ExerciseLog(string Name, int? Bpm, NoteValue? NoteValue);

/// <summary>One block (subcategory) on one day: time from the practice log plus the progress entry, if any.</summary>
public sealed record ProgressDay(DateOnly Date, int SecondsPractised, IReadOnlyList<ExerciseLog> Exercises, int? Rating, string? Notes)
{
    public bool HasActivity => SecondsPractised > 0 || Exercises.Count > 0 || Rating is not null || !string.IsNullOrWhiteSpace(Notes);
}

/// <summary>
/// Progress of one exercise within a block. Exercises are identified by name (trimmed, case- and space-insensitive)
/// <b>and</b> note value: 120 in sixteenths and 120 in eighths are different speeds, so they are tracked separately.
/// <see cref="Slot"/> is the order the exercise was first logged in this block (0-based); it never changes, so a
/// chart can colour by it and an exercise keeps its colour.
/// </summary>
public sealed record ExerciseProgress(
    string Key,
    string Name,
    NoteValue? NoteValue,
    int Slot,
    DateOnly FirstDate,
    DateOnly LastDate,
    int? BestBpm,
    int? LatestBpm,
    int? TargetBpm,
    bool HasOwnTarget,
    double? TargetFraction,
    int TimesLogged,
    IReadOnlyList<(DateOnly Date, int Bpm)> BpmHistory)
{
    public string Label => NoteValue is { } v ? $"{DisplayName} · {ProgressStats.NoteValueName(v)}" : DisplayName;
    public string DisplayName => string.IsNullOrEmpty(Name) ? "(unnamed)" : Name;
}

/// <summary>Headline numbers for one block.</summary>
/// <param name="Exercises">Per exercise, in first-logged order (= <see cref="ExerciseProgress.Slot"/>).</param>
/// <param name="Streak">Consecutive days with practice or a log entry, ending today (or yesterday, so it isn't
/// "broken" before you've practised today).</param>
public sealed record ProgressSummary(
    IReadOnlyList<ExerciseProgress> Exercises,
    double? RecentRating,
    int Streak,
    int MinutesThisWeek,
    int MinutesLast28Days,
    DateOnly? LastPractised,
    int DaysPractised)
{
    /// <summary>The exercise logged most recently (ties: the later line that day), if any.</summary>
    public ExerciseProgress? Latest => Exercises.OrderBy(e => e.LastDate).ThenBy(e => e.Slot).LastOrDefault();
}

public static class ProgressStats
{
    /// <summary>How many of the latest rated days <see cref="ProgressSummary.RecentRating"/> averages.</summary>
    public const int RecentRatings = 5;

    public static string NoteValueName(NoteValue value) => value switch
    {
        NoteValue.Quarters => "quarters",
        NoteValue.Eighths => "8ths",
        NoteValue.Triplets => "triplets",
        NoteValue.Sixteenths => "16ths",
        _ => value.ToString(),
    };

    /// <summary>Identity of an exercise: normalised name + note value.</summary>
    public static string ExerciseKey(string? name, NoteValue? noteValue) =>
        string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant()
        + "|" + (noteValue is { } v ? ((int)v).ToString(CultureInfo.InvariantCulture) : "");

    /// <param name="targetBpm">The block's target; used for exercises without their own.</param>
    /// <param name="exerciseTargets">The user's own targets by <see cref="ExerciseKey"/>.</param>
    public static ProgressSummary Summarize(
        IEnumerable<ProgressDay> history, int? startBpm, int? targetBpm, DateOnly today,
        IReadOnlyDictionary<string, int>? exerciseTargets = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        var days = history.Where(d => d.Date <= today).OrderBy(d => d.Date).ToList();

        // Per exercise, in first-logged order.
        var order = new List<string>();
        var byKey = new Dictionary<string, List<(DateOnly Date, ExerciseLog Line)>>();
        foreach (var day in days)
            foreach (var line in day.Exercises)
            {
                var key = ExerciseKey(line.Name, line.NoteValue);
                if (!byKey.TryGetValue(key, out var list))
                {
                    byKey[key] = list = [];
                    order.Add(key);
                }
                list.Add((day.Date, line));
            }

        var exercises = order.Select((key, slot) =>
        {
            var lines = byKey[key];
            var latestName = lines[^1].Line.Name.Trim();
            var bpms = lines.Where(l => l.Line.Bpm is not null).Select(l => (l.Date, Bpm: l.Line.Bpm!.Value)).ToList();
            var best = bpms.Count > 0 ? bpms.Max(b => b.Bpm) : (int?)null;
            var own = exerciseTargets is not null && exerciseTargets.TryGetValue(key, out var ownTarget) ? ownTarget : (int?)null;
            var effectiveTarget = own ?? targetBpm;
            double? fraction = null;
            if (best is int b && effectiveTarget is int target)
            {
                var from = startBpm ?? bpms[0].Bpm;
                fraction = target > from ? Math.Clamp((b - from) / (double)(target - from), 0, 1) : (b >= target ? 1 : 0);
            }
            // One point per day for charts: the day's best for this exercise.
            var history = bpms.GroupBy(x => x.Date).Select(g => (g.Key, g.Max(x => x.Bpm))).OrderBy(x => x.Key).ToList();
            return new ExerciseProgress(
                key, latestName, lines[0].Line.NoteValue, slot, lines[0].Date, lines[^1].Date,
                best, bpms.Count > 0 ? bpms[^1].Bpm : null, effectiveTarget, own is not null, fraction, lines.Count, history);
        }).ToList();

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
            exercises,
            recentRating,
            streak,
            minutesThisWeek,
            minutes28,
            practised.Count > 0 ? practised[^1].Date : null,
            practised.Count);
    }
}
