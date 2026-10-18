namespace GuitarPractice.Domain.Practice;

/// <summary>
/// What "Practice with metronome" sets up for a block: the exercise it is for (null = the block as a whole), the note
/// value to subdivide in, the tempo to start at and the tempo to ramp toward (null = no target above the start).
/// </summary>
public sealed record TempoPlan(string? ExerciseName, NoteValue? NoteValue, int? StartBpm, int? TargetBpm);

/// <summary>Links the metronome to the selected practice item: where to start, and writing the tempo reached back.</summary>
public static class PracticeTempo
{
    /// <summary>
    /// Picks the exercise and tempos. The exercise is the first named line in today's log (what you're about to play);
    /// without one, the exercise logged most recently. The start tempo is that line's BPM if typed, else the exercise's
    /// latest logged BPM, else the block's start BPM. The target is the exercise's own target, else the block's; it's
    /// dropped when it isn't above the start (nothing to ramp toward).
    /// </summary>
    /// <param name="todaysLines">The lines in the log strip right now (saved or not).</param>
    public static TempoPlan Plan(ProgressSummary summary, IReadOnlyList<ExerciseLog> todaysLines, int? blockStartBpm, int? blockTargetBpm)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(todaysLines);

        string? name;
        NoteValue? noteValue;
        int? typedBpm = null;
        if (todaysLines.FirstOrDefault(l => l.RemovedRun is null && !string.IsNullOrWhiteSpace(l.Name)) is { } line)
            (name, noteValue, typedBpm) = (line.Name.Trim(), line.NoteValue, line.Bpm);
        else if (summary.Latest is { } latest)
            (name, noteValue) = (latest.Name, latest.NoteValue);
        else
            (name, noteValue) = (null, null);

        var key = ProgressStats.ExerciseKey(name, noteValue);
        var known = summary.Exercises.FirstOrDefault(e => e.Key == key);

        var start = typedBpm ?? known?.LatestBpm ?? blockStartBpm;
        var target = known?.TargetBpm ?? blockTargetBpm;
        if (target is int t && start is int s && t <= s) target = null;
        return new TempoPlan(string.IsNullOrEmpty(name) ? null : name, noteValue, start, target);
    }

    /// <summary>
    /// Writes the tempo reached into the log lines: the line for the same exercise (name + note value) gets it; else the
    /// first empty line is filled in; else a line is added. Returns the new lines and the index of the one written.
    /// </summary>
    public static (IReadOnlyList<ExerciseLog> Lines, int Index) RecordReached(
        IReadOnlyList<ExerciseLog> lines, string? exerciseName, NoteValue? noteValue, int bpm)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var result = lines.ToList();
        var written = new ExerciseLog(exerciseName?.Trim() ?? "", bpm, noteValue);
        var key = ProgressStats.ExerciseKey(exerciseName, noteValue);

        var index = result.FindIndex(l => l.RemovedRun is null && ProgressStats.ExerciseKey(l.Name, l.NoteValue) == key); // never into a removed run
        if (index >= 0)
        {
            result[index] = result[index] with { Bpm = bpm };
            return (result, index);
        }

        index = result.FindIndex(l => l.RemovedRun is null && string.IsNullOrWhiteSpace(l.Name) && l.Bpm is null);
        if (index >= 0) result[index] = written;
        else
        {
            result.Add(written);
            index = result.Count - 1;
        }
        return (result, index);
    }
}
