namespace GuitarPractice.Audio.Tests;

public class TimeStretchTests
{
    private const int Rate = 48_000;

    private static float Rms(ReadOnlySpan<float> x)
    {
        var sum = 0.0;
        foreach (var v in x) sum += v * v;
        return (float)Math.Sqrt(sum / Math.Max(1, x.Length));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(0.9)]
    public void Slower_keeps_the_pitch_and_the_level(double speed)
    {
        var take = new AudioTake(YinPitchDetectorTests.Saw(196, Rate, Rate * 2, noise: 0), Rate); // G3 for 2 s
        var slow = TimeStretch.Stretch(take, speed)!;

        Assert.Equal((int)Math.Round(take.Samples.Length / speed), slow.Samples.Length);
        var yin = new YinPitchDetector(Rate);
        foreach (var at in new[] { 0.25, 0.5, 0.75 })
        {
            var start = (int)(slow.Samples.Length * at);
            var pitch = yin.Detect(slow.Samples.AsSpan(start, yin.RequiredSamples));
            Assert.InRange(pitch.Hz, 196 * 0.995, 196 * 1.005);                     // same note (< 10 ¢)
        }
        var middle = slow.Samples.AsSpan(slow.Samples.Length / 4, slow.Samples.Length / 2);
        Assert.InRange(Rms(middle) / Rms(take.Samples), 0.85f, 1.15f);               // no dips or swells
    }

    [Fact]
    public void Notes_land_later_by_one_over_the_speed()
    {
        // A pluck every 0.5 s → every 0.5 / 0.75 s.
        var samples = new float[Rate * 3];
        for (var beat = 0; beat < 6; beat++)
            for (var i = 0; i < Rate / 10; i++)
                samples[beat * Rate / 2 + i] = (float)(Math.Sin(2 * Math.PI * 330 * i / Rate) * Math.Exp(-i / (Rate * 0.02)));
        var slow = TimeStretch.Stretch(new AudioTake(samples, Rate), 0.75)!;

        for (var beat = 1; beat < 5; beat++)
        {
            var expected = beat * 0.5 / 0.75;
            var from = (int)((expected - 0.15) * Rate);
            var onset = from;
            while (onset < slow.Samples.Length && Math.Abs(slow.Samples[onset]) < 0.2f) onset++;
            Assert.InRange(onset / (double)Rate, expected - 0.025, expected + 0.025);
        }
    }

    [Fact]
    public void Full_speed_is_the_same_take_and_a_cancelled_stretch_returns_null()
    {
        var take = new AudioTake(YinPitchDetectorTests.Sine(220, Rate, Rate), Rate);
        Assert.Same(take, TimeStretch.Stretch(take, 1.0));
        Assert.Equal(take.Samples.Length * 2, TimeStretch.Stretch(take, 0.1)!.Samples.Length); // clamped to 50 %
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Null(TimeStretch.Stretch(take, 0.5, cancel: cancelled.Token));                    // no exception: routine
    }
}
