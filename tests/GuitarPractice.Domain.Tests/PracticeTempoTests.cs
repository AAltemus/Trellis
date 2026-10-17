using GuitarPractice.Domain.Practice;

namespace GuitarPractice.Domain.Tests;

public class PracticeTempoTests
{
    private static readonly DateOnly Today = new(2026, 10, 17);

    private static ExerciseLog Ex(string name, int? bpm, NoteValue? value = NoteValue.Sixteenths) => new(name, bpm, value);

    private static ProgressSummary History(IReadOnlyDictionary<string, int>? targets = null, int? blockTarget = null) =>
        ProgressStats.Summarize(
        [
            new ProgressDay(Today.AddDays(-3), 600, [Ex("Rolling sweeps", 80), Ex("Trills", 150)], null, null),
            new ProgressDay(Today.AddDays(-1), 600, [Ex("Rolling sweeps", 90), Ex("Scale", 120, NoteValue.Eighths)], null, null),
        ], 60, blockTarget, Today, targets);

    [Fact]
    public void Without_lines_today_it_continues_the_most_recently_logged_exercise()
    {
        var plan = PracticeTempo.Plan(History(), [Ex("", null, null)], blockStartBpm: 60, blockTargetBpm: 160);

        Assert.Equal(new TempoPlan("Scale", NoteValue.Eighths, 120, 160), plan);
    }

    [Fact]
    public void The_first_named_line_today_picks_the_exercise_and_its_latest_tempo()
    {
        var plan = PracticeTempo.Plan(History(), [Ex("  ", 50), Ex(" rolling sweeps ", null)], 60, 160);

        Assert.Equal(new TempoPlan("rolling sweeps", NoteValue.Sixteenths, 90, 160), plan);
    }

    [Fact]
    public void A_typed_bpm_wins_over_history()
    {
        var plan = PracticeTempo.Plan(History(), [Ex("Trills", 155)], 60, 200);

        Assert.Equal(155, plan.StartBpm);
    }

    [Fact]
    public void An_own_target_beats_the_block_target()
    {
        var targets = new Dictionary<string, int> { [ProgressStats.ExerciseKey("Rolling sweeps", NoteValue.Sixteenths)] = 120 };

        var plan = PracticeTempo.Plan(History(targets, blockTarget: 160), [Ex("Rolling sweeps", null)], 60, 160);

        Assert.Equal((90, 120), (plan.StartBpm, plan.TargetBpm));
    }

    [Fact]
    public void A_new_exercise_starts_at_the_block_start_tempo()
    {
        var plan = PracticeTempo.Plan(History(), [Ex("Legato runs", null, NoteValue.Triplets)], 70, 140);

        Assert.Equal(new TempoPlan("Legato runs", NoteValue.Triplets, 70, 140), plan);
    }

    [Fact]
    public void A_target_at_or_below_the_start_is_dropped()
    {
        Assert.Null(PracticeTempo.Plan(History(), [Ex("Trills", null)], 60, 150).TargetBpm);  // latest 150 = target
        Assert.Null(PracticeTempo.Plan(History(), [Ex("Trills", null)], 60, 140).TargetBpm);
    }

    [Fact]
    public void An_empty_block_has_no_exercise_and_uses_the_block_tempos()
    {
        var empty = ProgressStats.Summarize([], null, null, Today);

        Assert.Equal(new TempoPlan(null, null, 60, 100), PracticeTempo.Plan(empty, [], 60, 100));
        Assert.Equal(new TempoPlan(null, null, null, 100), PracticeTempo.Plan(empty, [], null, 100));
    }

    [Fact]
    public void Reached_tempo_updates_the_same_exercise_line()
    {
        var (lines, index) = PracticeTempo.RecordReached(
            [Ex("Scale", 100, NoteValue.Eighths), Ex("Trills", 140)], " TRILLS", NoteValue.Sixteenths, 152);

        Assert.Equal(1, index);
        Assert.Equal([Ex("Scale", 100, NoteValue.Eighths), Ex("Trills", 152)], lines);
    }

    [Fact]
    public void Same_name_other_note_value_is_not_the_same_line()
    {
        var (lines, index) = PracticeTempo.RecordReached([Ex("Trills", 140)], "Trills", NoteValue.Eighths, 200);

        Assert.Equal(1, index);
        Assert.Equal([Ex("Trills", 140), Ex("Trills", 200, NoteValue.Eighths)], lines);
    }

    [Fact]
    public void Reached_tempo_fills_the_first_empty_line_before_adding_one()
    {
        var (lines, index) = PracticeTempo.RecordReached([Ex("Scale", 100), Ex(" ", null, null)], "Sweeps", NoteValue.Triplets, 96);

        Assert.Equal(1, index);
        Assert.Equal([Ex("Scale", 100), Ex("Sweeps", 96, NoteValue.Triplets)], lines);
    }

    [Fact]
    public void A_block_without_an_exercise_writes_an_unnamed_line()
    {
        var (lines, index) = PracticeTempo.RecordReached([], null, null, 88);

        Assert.Equal(0, index);
        Assert.Equal([Ex("", 88, null)], lines);
    }
}
