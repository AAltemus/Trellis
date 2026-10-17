namespace GuitarPractice.Audio.Tests;

/// <summary>Checks that audio-thread code doesn't allocate once it is running.</summary>
internal static class Allocations
{
    /// <summary>
    /// Runs <paramref name="step"/> <paramref name="warmUp"/> times and pauses, so tiered JIT has promoted it (after 30
    /// calls, recompiled on a background thread) and on-stack replacement has happened; then asserts that
    /// <paramref name="iterations"/> more calls allocate nothing on this thread. With a single warm-up call these tests
    /// occasionally measured a few KB of one-off JIT allocations when the whole suite ran in parallel (2026-10-17).
    /// </summary>
    public static void AssertNone(Action step, int iterations = 2_000, int warmUp = 2_000)
    {
        ArgumentNullException.ThrowIfNull(step);
        for (var i = 0; i < warmUp; i++) step();
        Thread.Sleep(200);
        for (var i = 0; i < 100; i++) step();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++) step();
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
