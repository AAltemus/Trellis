using GuitarPractice.Domain.Rhythm;

namespace GuitarPractice.Domain.Tests;

public sealed class RhythmTests
{
    private const int Q = Meter.TicksPerQuarter;

    [Fact]
    public void Meters_click_on_their_groups()
    {
        Assert.Equal([0, Q, 2 * Q, 3 * Q], Meter.FourFour.BeatTicks);
        Assert.Equal([0, 3 * Q / 2], Meter.SixEight.BeatTicks);                       // two dotted quarters
        Assert.Equal([0, Q, 2 * Q], Meter.SevenEight.BeatTicks);                       // 2+2+3 eighths
        Assert.Equal(7 * Q / 2, Meter.SevenEight.BarTicks);
        Assert.Equal("7/8 (2+2+3)", Meter.SevenEight.Label);
        Assert.Equal("4/4", Meter.FourFour.Label);
        Assert.Equal(2, Meter.SevenEight.BeatAt(2 * Q + 10));
        Assert.Same(Meter.SevenEight, Meter.Find("7/8"));
        Assert.True(Meter.FiveFour.IsOdd && Meter.SevenEight.IsOdd && !Meter.SixEight.IsOdd);
    }

    [Fact]
    public void Every_meter_has_built_in_rhythms_that_fill_the_bar()
    {
        foreach (var meter in Meter.All)
        {
            var patterns = RhythmLibrary.For(meter);
            Assert.True(patterns.Count >= 3, $"{meter.Signature}: {patterns.Count} rhythms");
            Assert.Contains(patterns, p => p.Level == 1);
            foreach (var p in patterns)
            {
                Assert.Equal(meter.BarTicks, p.Notes.Sum(n => n.Ticks));
                Assert.NotEmpty(p.Onsets);
            }
        }
        // Names are unique within a meter (they're how a pick is remembered).
        Assert.All(RhythmLibrary.All.GroupBy(p => (p.Meter.Signature, p.Name)), g => Assert.Single(g));
    }

    [Fact]
    public void Parse_reads_values_dots_rests_ties_and_tuplets()
    {
        var p = RhythmPattern.Parse("x", 1, Meter.FourFour, "q e re e~ e [3 e e e]");
        Assert.Equal([Q, Q / 2, Q / 2, Q / 2, Q / 2, Q / 3, Q / 3, Q / 3], p.Notes.Select(n => n.Ticks));
        Assert.True(p.Notes[2].IsRest);
        Assert.True(p.Notes[3].TiedToNext);
        Assert.Equal(new Tuplet(3, 2, 0), p.Notes[5].Tuplet);
        // Played: q (0), e (1), e~ (2) — the rest at 1.5 and the tied e at 2.5 aren't struck — then the triplet at 3, 3⅓, 3⅔.
        Assert.Equal([0, Q, 2 * Q, 3 * Q, 3 * Q + Q / 3, 3 * Q + 2 * Q / 3], p.Onsets);
        Assert.Equal(3, p.NoteIndexOfOnset(2));
    }

    [Fact]
    public void Quintuplets_and_32nds_land_on_whole_ticks()
    {
        var p = RhythmPattern.Parse("x", 1, Meter.TwoFour, "[5 s s s s s] t t t t e");
        Assert.All(p.Notes.Take(5), n => Assert.Equal(Q / 5, n.Ticks));
        Assert.Equal(Q / 8, p.Notes[5].Ticks);
    }

    [Theory]
    [InlineData("q q q")]          // short
    [InlineData("q q q q q")]      // long
    [InlineData("q x q q")]        // not a note
    [InlineData("[3 e e e q q q")] // unclosed
    [InlineData("[4 s s s s] q q q")] // unsupported tuplet
    [InlineData("q q q q~")]       // tie out of the bar
    [InlineData("rq~ q q q")]      // tied rest
    public void Parse_refuses_bad_rhythms(string text) =>
        Assert.Throws<FormatException>(() => RhythmPattern.Parse("bad", 1, Meter.FourFour, text));

    [Fact]
    public void Score_matches_played_notes_to_the_nearest_expected_one()
    {
        double[] expected = [0.0, 0.5, 1.0, 1.5];
        // 10 ms late, 40 ms early, missed, 5 ms late; plus a stray note far from any.
        var result = RhythmScore.Score(expected, [0.010, 0.460, 1.250, 1.505]);

        Assert.Equal([10.0, -40.0, null, 5.0], result.Notes.Select(n => n.OffsetMs.HasValue ? Math.Round(n.OffsetMs.Value) : (double?)null));
        Assert.Equal((3, 1, 1), (result.Hits, result.Missed, result.Extra));
        Assert.Equal(100, result.WindowMs);
        Assert.Equal(-25.0 / 3, result.MeanOffsetMs!.Value, 6);
        Assert.Equal(55.0 / 3, result.MeanAbsOffsetMs!.Value, 6);
        // Points: 1 (within 20 ms) + (1 − 20/80) + 0 + 1 = 2.75 over 4 notes + 1 extra = 55 %.
        Assert.Equal(55, result.Percent);
    }

    [Fact]
    public void Score_is_100_for_a_tight_take_and_counts_doubles_as_extra()
    {
        double[] expected = [0, 0.25, 0.5, 0.75];
        Assert.Equal(100, RhythmScore.Score(expected, [0.005, 0.24, 0.51, 0.75]).Percent);

        var doubled = RhythmScore.Score(expected, [0.0, 0.02, 0.25, 0.5, 0.75]); // two strums for the first note
        Assert.Equal((4, 1), (doubled.Hits, doubled.Extra));
        Assert.Equal(0.0, doubled.Notes[0].OffsetMs);                             // the closer one counts
    }

    [Fact]
    public void Fast_rhythms_narrow_the_window_so_neighbours_cant_be_credited()
    {
        // 16ths at 120 BPM are 125 ms apart: window 45 % = 56 ms.
        double[] expected = [0, 0.125, 0.25, 0.375];
        Assert.Equal(56.25, RhythmScore.Window(expected), 6);
        var result = RhythmScore.Score(expected, [0.0, 0.125 + 0.06, 0.25, 0.375]);
        Assert.Equal((3, 1), (result.Hits, result.Extra));
        Assert.Null(result.Notes[1].OffsetMs);
    }

    [Fact]
    public void Nothing_played_scores_zero_with_every_note_missed()
    {
        var result = RhythmScore.Score([0, 1, 2], []);
        Assert.Equal((0, 3, 0), (result.Percent, result.Missed, result.Extra));
        Assert.Null(result.MeanOffsetMs);
    }
}
