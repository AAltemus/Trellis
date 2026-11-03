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
                Assert.Equal(p.Bars * meter.BarTicks, p.Notes.Sum(n => n.Ticks));
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
    public void A_note_played_way_off_is_one_miss_not_a_miss_and_an_extra()
    {
        // At ♩ = 100 (window 100 ms): the 3& (after a quarter rest) played 160 ms early, the last 8th played 180 ms late.
        double[] expected = [0, 0.3, 1.5, 1.8, 2.1, 2.4];
        var r = RhythmScore.Score(expected, [0, 0.3, 1.34, 1.8, 2.1, 2.58]);
        Assert.Equal((4, 2, 0), (r.Hits, r.WayOff, r.Extra));
        Assert.Equal(-160, r.Notes[2].WayOffMs!.Value, 6);
        Assert.Equal(180, r.Notes[5].WayOffMs!.Value, 6);                   // the last note: half the gap before it
        Assert.False(r.Notes[2].IsHit);
        Assert.Equal(67, r.Percent);                                         // 4 points over 6 notes (was 4 over 8)

        // On an 8th grid, 160 ms early is nearer the note before (already hit): still taken as this note, since the
        // other one was played.
        var eighths = RhythmScore.Score([0, 0.3, 0.6, 0.9], [0, 0.3, 0.44, 0.9]);
        Assert.Equal((3, 1, 0), (eighths.Hits, eighths.WayOff, eighths.Extra));
        Assert.Equal(-160, eighths.Notes[2].WayOffMs!.Value, 6);

        // Beyond twice the window, or with nothing missed beside it: still extra.
        Assert.Equal((2, 0, 1), (RhythmScore.Score([0, 1], [0, 1, 1.25]) is var a ? (a.Hits, a.WayOff, a.Extra) : default));
        Assert.Equal((0, 1), (RhythmScore.Score([0, 1, 2], [0, 1.25, 2]) is var b ? (b.WayOff, b.Extra) : default));

        // Two candidates for one missed note: the nearer one is the note, the other stays extra.
        var two = RhythmScore.Score(expected, [0, 0.3, 1.35, 1.38, 1.8, 2.1, 2.4]);
        Assert.Equal((5, 1, 1), (two.Hits, two.WayOff, two.Extra));
        Assert.Equal(-120, two.Notes[2].WayOffMs!.Value, 6);
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
        Assert.Equal((3, 0), (result.Hits, result.Extra));
        Assert.Null(result.Notes[1].OffsetMs);
        Assert.Equal(60, result.Notes[1].WayOffMs!.Value, 6);  // just outside: counted once, as played way off
    }

    [Fact]
    public void Nothing_played_scores_zero_with_every_note_missed()
    {
        var result = RhythmScore.Score([0, 1, 2], []);
        Assert.Equal((0, 3, 0), (result.Percent, result.Missed, result.Extra));
        Assert.Null(result.MeanOffsetMs);
    }

    [Fact]
    public void Bar_lines_make_a_multi_bar_rhythm_and_ties_may_cross_them()
    {
        var p = RhythmPattern.Parse("push", 4, Meter.FourFour, "q q q e e~ | h h");
        Assert.Equal(2, p.Bars);
        Assert.Equal(2 * 1920, p.Length);
        Assert.Equal([0, 480, 960, 1440, 1680, 2880], p.Onsets);            // the tied 8th's half note isn't struck
        Assert.Equal("q q q e e~ | h h", p.Text);

        var bad = Assert.Throws<FormatException>(() => RhythmPattern.Parse("x", 1, Meter.FourFour, "q q q | q q q q"));
        Assert.Contains("bar 1's notes add up to 1440 ticks", bad.Message, StringComparison.Ordinal);
        Assert.Contains("(3 beats of 4 beats)", bad.Message, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => RhythmPattern.Parse("x", 1, Meter.TwoFour, "[3 e e | e] q q"));   // tuplet across a bar line
        Assert.Throws<FormatException>(() => RhythmPattern.Parse("x", 1, Meter.TwoFour, "h | h | h | h | h")); // more than 4 bars
        Assert.Contains("3.5 eighths of 6 eighths",
            Assert.Throws<FormatException>(() => RhythmPattern.Parse("x", 1, Meter.SixEight, "q e.")).Message, StringComparison.Ordinal);
        Assert.All(RhythmLibrary.All.Where(r => r.Name.EndsWith("(2 bars)", StringComparison.Ordinal)), r => Assert.Equal(2, r.Bars));

        // Typed text is checked without exceptions.
        Assert.False(RhythmPattern.TryParse("mine", 0, Meter.FourFour, "q q x", out var none, out var error));
        Assert.Null(none);
        Assert.Equal("'x' isn't a note (w h q e s t, r for a rest, . for a dot, ~ for a tie, | between bars).", error);
        Assert.True(RhythmPattern.TryParse("mine", 0, Meter.FourFour, "  q q\th  ", out var ok, out error));
        Assert.Null(error);
        Assert.Equal("q q h", ok!.Text);
    }

    [Fact]
    public void Random_rhythms_fill_the_bar_at_their_level_in_every_meter()
    {
        foreach (var meter in Meter.All)
            for (var level = RhythmGenerator.MinLevel; level <= RhythmGenerator.MaxLevel; level++)
                for (var seed = 0; seed < 60; seed++)
                {
                    var p = RhythmGenerator.Generate(meter, level, new Random(seed), bars: seed % 2 + 1);
                    Assert.Equal(level, p.Level);
                    Assert.Equal(seed % 2 + 1, p.Bars);
                    Assert.True(p.Onsets.Count >= 2, $"{meter.Signature} L{level}: {p.Text}");
                    if (level == 1 && meter.Denominator == 4)                    // quarters, halves, quarter rests only
                        Assert.All(p.Notes, n => Assert.True(n.Length <= NoteLength.Quarter && n.Tuplet is null, p.Text));
                    if (level == 5)                                              // always something only level 5 has
                        Assert.Contains(p.Notes, n => n.Length == NoteLength.ThirtySecond || n.Tuplet is { Actual: 5 or 6 }
                            || n.Tuplet is { Actual: 3 } && n.Length == NoteLength.Sixteenth);
                }

        var a = RhythmGenerator.Generate(Meter.FourFour, 3, new Random(42));
        var b = RhythmGenerator.Generate(Meter.FourFour, 3, new Random(42));
        Assert.Equal(a.Text, b.Text);
        Assert.Equal("Random · level 3", a.Name);
    }

    [Fact]
    public void Builder_fills_bars_refuses_what_wont_fit_and_writes_the_text()
    {
        var b = new RhythmBuilder(Meter.FourFour);
        Assert.True(b.IsEmpty);
        Assert.Equal(4 * Q, b.LeftInBar);
        Assert.True(b.Add(NoteLength.Quarter));
        Assert.True(b.Add(NoteLength.Eighth, triplet: true));
        Assert.True(b.Add(NoteLength.Eighth, dotted: true));
        Assert.True(b.Add(NoteLength.Sixteenth));
        Assert.Equal(Q, b.LeftInBar);
        Assert.False(b.CanAdd(NoteLength.Half, false, false));       // doesn't fit in the beat that's left
        Assert.False(b.Add(NoteLength.Half));
        Assert.False(b.IsComplete);
        Assert.True(b.Add(NoteLength.Quarter, rest: true));
        Assert.True(b.IsComplete);
        Assert.Equal(1, b.CurrentBar);
        Assert.True(b.Add(NoteLength.Half));
        b.ToggleTie();                                                 // tie into the next note
        Assert.True(b.IsLastTied);
        Assert.Equal(2, b.Bars);
        Assert.Equal(2, b.Preview.Bars);                               // a short bar draws fine
        Assert.True(b.Add(NoteLength.Half));
        Assert.Equal("q [3 e e e] e. s rq | h~ h", b.Text);
        Assert.True(RhythmPattern.TryParse("mine", 0, Meter.FourFour, b.Text, out var p, out _));
        Assert.Equal(7, p!.Onsets.Count);                              // the tied half isn't struck again
        Assert.Equal(3, b.Preview.Bars);                               // the next (empty) bar is shown

        b.Undo();
        Assert.Equal("q [3 e e e] e. s rq | h~", b.Text);
        b.ToggleTie();
        Assert.False(b.IsLastTied);
        b.Undo();
        b.Undo();
        Assert.Equal("q [3 e e e] e. s", b.Text);
        Assert.True(b.CanTie);                                         // the last is a 16th now
        b.Add(NoteLength.Quarter, rest: true);
        Assert.False(b.CanTie);                                        // a rest can't be tied
        b.Clear();
        Assert.True(b.IsEmpty);
        Assert.Empty(b.Preview.Notes);
    }

    [Fact]
    public void Builder_stops_at_four_bars_and_loads_a_copy_of_any_rhythm()
    {
        var b = new RhythmBuilder(Meter.SixEight);
        for (var i = 0; i < RhythmPattern.MaxBars; i++) Assert.True(b.Add(NoteLength.Half, dotted: true));
        Assert.Equal(0, b.LeftInBar);
        Assert.False(b.Add(NoteLength.ThirtySecond));

        foreach (var meter in Meter.All)
            foreach (var pattern in RhythmLibrary.For(meter))
            {
                var copy = RhythmBuilder.From(pattern);
                Assert.True(copy.IsComplete);
                Assert.True(RhythmPattern.TryParse("copy", 0, meter, copy.Text, out var again, out _), pattern.Name);
                Assert.Equal(pattern.Onsets, again!.Onsets);
                Assert.Equal(pattern.Notes.Select(n => (n.Start, n.Ticks, n.IsRest, n.TiedToNext)),
                    again.Notes.Select(n => (n.Start, n.Ticks, n.IsRest, n.TiedToNext)));
            }
    }

    [Fact]
    public void A_draft_may_end_short_or_tied_but_earlier_bars_must_be_full()
    {
        Assert.True(RhythmPattern.TryParseDraft(Meter.FourFour, "", out var empty, out _));
        Assert.Empty(empty!.Notes);
        Assert.True(RhythmPattern.TryParseDraft(Meter.FourFour, "h h | q~", out var short_, out _));
        Assert.Equal(2, short_!.Bars);
        Assert.False(RhythmPattern.TryParseDraft(Meter.FourFour, "h | q", out _, out var error));
        Assert.Contains("bar 1", error, StringComparison.Ordinal);
        Assert.False(RhythmPattern.TryParseDraft(Meter.FourFour, "w q", out _, out _));
        Assert.False(RhythmPattern.TryParse("x", 0, Meter.FourFour, "h q", out _, out _)); // a full rhythm still must fill it
    }
}
