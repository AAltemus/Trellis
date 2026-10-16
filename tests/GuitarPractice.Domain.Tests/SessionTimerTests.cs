using GuitarPractice.Domain.Practice;

namespace GuitarPractice.Domain.Tests;

public class SessionTimerTests
{
    /// <summary>Monotonic clock that only moves when told to.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }

    private readonly ManualClock _clock = new();

    private SessionTimer Loaded(int plannedMinutes, int elapsedSeconds = 0, bool completed = false)
    {
        var timer = new SessionTimer(_clock);
        timer.Load(plannedMinutes, elapsedSeconds, completed);
        return timer;
    }

    [Fact]
    public void Counts_down_from_start()
    {
        var timer = Loaded(1);
        timer.Start();
        _clock.Advance(59);

        Assert.True(timer.Tick().HasFlag(TimerTickResult.ElapsedChanged));
        Assert.Equal(59, timer.ElapsedSeconds);
        Assert.Equal(1, timer.RemainingSeconds);
        Assert.True(timer.IsRunning);
    }

    [Fact]
    public void Resumes_from_saved_progress()
    {
        var timer = Loaded(10, elapsedSeconds: 120);
        timer.Start();
        _clock.Advance(30);
        timer.Tick();
        Assert.Equal(150, timer.ElapsedSeconds);
    }

    [Fact]
    public void Missed_ticks_lose_no_time()
    {
        var timer = Loaded(10);
        timer.Start();
        _clock.Advance(42.7); // one tick after a long stall
        timer.Tick();
        Assert.Equal(42, timer.ElapsedSeconds);
    }

    [Fact]
    public void Pause_stops_accrual_and_resume_continues()
    {
        var timer = Loaded(10);
        timer.Start();
        _clock.Advance(20);
        timer.Pause();
        Assert.False(timer.IsRunning);
        Assert.Equal(20, timer.ElapsedSeconds);

        _clock.Advance(300);
        Assert.Equal(TimerTickResult.None, timer.Tick());
        Assert.Equal(20, timer.ElapsedSeconds);

        timer.Start();
        _clock.Advance(5);
        timer.Tick();
        Assert.Equal(25, timer.ElapsedSeconds);
    }

    [Fact]
    public void Fractions_of_a_second_survive_pauses()
    {
        var timer = Loaded(10);
        for (var i = 0; i < 4; i++)
        {
            timer.Start();
            _clock.Advance(0.5);
            timer.Pause();
        }
        Assert.Equal(2, timer.ElapsedSeconds);
    }

    [Fact]
    public void Finishes_exactly_once_at_zero()
    {
        var timer = Loaded(1, elapsedSeconds: 50);
        timer.Start();
        _clock.Advance(9);
        Assert.False(timer.Tick().HasFlag(TimerTickResult.Finished));

        _clock.Advance(1.5);
        var result = timer.Tick();
        Assert.True(result.HasFlag(TimerTickResult.Finished));
        Assert.True(timer.Completed);
        Assert.False(timer.IsRunning);
        Assert.Equal(60, timer.ElapsedSeconds); // clamped to the plan
        Assert.Equal(0, timer.RemainingSeconds);

        _clock.Advance(10);
        Assert.Equal(TimerTickResult.None, timer.Tick());
        Assert.False(timer.CanStart);
    }

    [Fact]
    public void Stop_part_way_keeps_elapsed_and_completes()
    {
        var timer = Loaded(10);
        timer.Start();
        _clock.Advance(90);
        Assert.True(timer.CanStop);
        timer.Stop();

        Assert.False(timer.IsRunning);
        Assert.True(timer.Completed);
        Assert.Equal(90, timer.ElapsedSeconds);
        Assert.Equal(0, timer.RemainingSeconds);
        Assert.False(timer.CanStop);
        Assert.True(timer.CanReset);
    }

    [Fact]
    public void Reset_clears_progress()
    {
        var timer = Loaded(10, elapsedSeconds: 200, completed: true);
        Assert.True(timer.CanReset);
        timer.Reset();

        Assert.Equal(0, timer.ElapsedSeconds);
        Assert.False(timer.Completed);
        Assert.Equal(600, timer.RemainingSeconds);
        Assert.True(timer.CanStart);
        Assert.False(timer.CanReset);
    }

    [Fact]
    public void Reset_and_load_are_refused_while_running()
    {
        var timer = Loaded(10);
        timer.Start();
        Assert.False(timer.CanReset);
        Assert.Throws<InvalidOperationException>(timer.Reset);
        Assert.Throws<InvalidOperationException>(() => timer.Load(5, 0, false));
    }

    [Fact]
    public void Autosave_every_15_seconds_of_running_time()
    {
        var timer = Loaded(10);
        timer.Start();

        _clock.Advance(14);
        Assert.False(timer.Tick().HasFlag(TimerTickResult.AutosaveDue));
        _clock.Advance(1);
        Assert.True(timer.Tick().HasFlag(TimerTickResult.AutosaveDue));
        _clock.Advance(14);
        Assert.False(timer.Tick().HasFlag(TimerTickResult.AutosaveDue));
        _clock.Advance(1);
        Assert.True(timer.Tick().HasFlag(TimerTickResult.AutosaveDue));
    }

    [Fact]
    public void Paused_wall_time_does_not_trigger_autosave()
    {
        var timer = Loaded(10);
        timer.Start();
        _clock.Advance(10);
        timer.Tick();
        timer.Pause(); // the caller saves on pause

        _clock.Advance(120);
        Assert.Equal(TimerTickResult.None, timer.Tick());

        timer.Start();
        _clock.Advance(10);
        Assert.False(timer.Tick().HasFlag(TimerTickResult.AutosaveDue));
        _clock.Advance(5);
        Assert.True(timer.Tick().HasFlag(TimerTickResult.AutosaveDue));
    }

    [Fact]
    public void Plan_shrinking_below_elapsed_finishes_on_next_tick()
    {
        var timer = Loaded(10);
        timer.Start();
        _clock.Advance(400);
        timer.Tick();

        timer.UpdatePlannedMinutes(5); // regenerate gave this item 5 min; 6:40 already practised
        _clock.Advance(0.2);
        var result = timer.Tick();

        Assert.True(result.HasFlag(TimerTickResult.Finished));
        Assert.True(timer.Completed);
        Assert.Equal(300, timer.ElapsedSeconds);
    }

    [Fact]
    public void Cannot_start_a_done_or_unloaded_item()
    {
        Assert.False(new SessionTimer(_clock).CanStart);
        Assert.False(Loaded(10, elapsedSeconds: 600).CanStart);
        Assert.False(Loaded(10, completed: true).CanStart);
        Assert.Throws<InvalidOperationException>(new SessionTimer(_clock).Start);
    }

    [Fact]
    public void Unload_while_running_keeps_the_time()
    {
        var timer = Loaded(10);
        timer.Start();
        _clock.Advance(33);
        timer.Unload();

        Assert.False(timer.IsRunning);
        Assert.False(timer.IsLoaded);
        Assert.Equal(33, timer.ElapsedSeconds);
    }

    [Theory]
    [InlineData(new[] { false, false, false }, 0, 1)]   // next one after current
    [InlineData(new[] { false, true, false }, 0, 2)]    // skips done items
    [InlineData(new[] { false, true, true }, 2, 0)]     // wraps to an earlier unfinished item
    [InlineData(new[] { true, true, true }, 1, -1)]     // all done
    [InlineData(new[] { false }, 0, 0)]                 // only itself (if still unfinished)
    public void Next_unfinished_item(bool[] done, int current, int expected) =>
        Assert.Equal(expected, SessionOrder.NextUnfinished(done, current, d => d));
}
