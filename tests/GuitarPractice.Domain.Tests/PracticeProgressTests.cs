using GuitarPractice.Domain.Planning;

namespace GuitarPractice.Domain.Tests;

public class PracticeProgressTests
{
    [Fact]
    public void Remaining_counts_down_and_clamps_at_zero()
    {
        Assert.Equal(600, PracticeProgress.RemainingSeconds(10, 0, completed: false));
        Assert.Equal(455, PracticeProgress.RemainingSeconds(10, 145, completed: false));
        Assert.Equal(0, PracticeProgress.RemainingSeconds(10, 700, completed: false));
        Assert.Equal(600, PracticeProgress.RemainingSeconds(10, -5, completed: false));
    }

    [Fact]
    public void Completed_items_have_nothing_left_even_if_stopped_early()
    {
        Assert.Equal(0, PracticeProgress.RemainingSeconds(20, 90, completed: true));
        Assert.True(PracticeProgress.IsDone(20, 90, completed: true));
        Assert.Equal(1, PracticeProgress.Fraction(20, 90, completed: true));
    }

    [Fact]
    public void Fraction_tracks_elapsed_share()
    {
        Assert.Equal(0.25, PracticeProgress.Fraction(10, 150, completed: false), 6);
        Assert.Equal(1, PracticeProgress.Fraction(10, 9999, completed: false));
    }

    [Fact]
    public void Format_uses_minutes_and_hours()
    {
        Assert.Equal("0:00", PracticeProgress.Format(0));
        Assert.Equal("4:05", PracticeProgress.Format(245));
        Assert.Equal("59:59", PracticeProgress.Format(3599));
        Assert.Equal("1:02:03", PracticeProgress.Format(3723));
        Assert.Equal("0:00", PracticeProgress.Format(-10));
    }
}
