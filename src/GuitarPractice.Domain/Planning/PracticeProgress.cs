namespace GuitarPractice.Domain.Planning;

/// <summary>Pure countdown math shared by the session runner and the week view.</summary>
public static class PracticeProgress
{
    /// <summary>Seconds left on an item: 0 once completed or once elapsed reaches the planned time.</summary>
    public static int RemainingSeconds(int plannedMinutes, int elapsedSeconds, bool completed)
    {
        if (completed) return 0;
        return Math.Max(0, plannedMinutes * 60 - Math.Max(0, elapsedSeconds));
    }

    public static bool IsDone(int plannedMinutes, int elapsedSeconds, bool completed) =>
        RemainingSeconds(plannedMinutes, elapsedSeconds, completed) == 0;

    /// <summary>0..1 share of the planned time already practised (1 when completed).</summary>
    public static double Fraction(int plannedMinutes, int elapsedSeconds, bool completed)
    {
        if (completed || plannedMinutes <= 0) return 1;
        return Math.Clamp(elapsedSeconds / (plannedMinutes * 60.0), 0, 1);
    }

    /// <summary>"4:05", "59:59", "1:02:03".</summary>
    public static string Format(int seconds)
    {
        seconds = Math.Max(0, seconds);
        var h = seconds / 3600;
        var m = seconds / 60 % 60;
        var s = seconds % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }
}
