using GuitarPractice.Audio;

namespace GuitarPractice.Audio.Tests;

public class YinPitchDetectorTests
{
    internal static float[] Sine(double hz, int rate, int n, double amp = 0.5, double phase = 0.3)
    {
        var x = new float[n];
        for (var i = 0; i < n; i++) x[i] = (float)(amp * Math.Sin(2 * Math.PI * hz * i / rate + phase));
        return x;
    }

    /// <summary>Band-limited sawtooth (harmonics 1/k up to Nyquist) plus white noise: a stand-in for a plucked string.</summary>
    internal static float[] Saw(double hz, int rate, int n, double noise = 0.05, int seed = 1)
    {
        var rng = new Random(seed);
        var x = new float[n];
        var harmonics = (int)(rate / 2 / hz);
        for (var i = 0; i < n; i++)
        {
            double v = 0;
            for (var k = 1; k <= Math.Min(harmonics, 40); k++) v += Math.Sin(2 * Math.PI * k * hz * i / rate) / k;
            x[i] = (float)(0.3 * v + noise * (rng.NextDouble() * 2 - 1));
        }
        return x;
    }

    private static double Cents(double measured, double expected) => 1200 * Math.Log2(measured / expected);

    [Theory]
    [InlineData(48_000, 73.42)]    // D2 (drop D)
    [InlineData(48_000, 82.41)]    // E2
    [InlineData(48_000, 110.00)]   // A2
    [InlineData(48_000, 146.83)]   // D3
    [InlineData(48_000, 196.00)]   // G3
    [InlineData(48_000, 246.94)]   // B3
    [InlineData(48_000, 329.63)]   // E4
    [InlineData(48_000, 440.00)]
    [InlineData(48_000, 987.77)]   // B5, 19th fret high E
    [InlineData(44_100, 82.41)]
    [InlineData(44_100, 329.63)]
    [InlineData(44_100, 103.83)]   // G#2: period not a whole number of samples
    public void Sines_are_within_one_cent(int rate, double hz)
    {
        var detector = new YinPitchDetector(rate);
        var estimate = detector.Detect(Sine(hz, rate, detector.RequiredSamples));

        Assert.True(estimate.IsVoiced);
        Assert.InRange(Cents(estimate.Hz, hz), -1, 1);
        Assert.True(estimate.Confidence > 0.9, $"confidence {estimate.Confidence}");
    }

    [Theory]
    [InlineData(61.74)]  // B1, 7-string / B standard
    [InlineData(65.41)]  // C2, drop C
    public void Lowest_tuning_notes_are_found(double hz)
    {
        var detector = new YinPitchDetector(48_000);
        var estimate = detector.Detect(Sine(hz, 48_000, detector.RequiredSamples));
        Assert.InRange(Cents(estimate.Hz, hz), -2, 2);
    }

    [Theory]
    [InlineData(82.41)]
    [InlineData(61.74)]
    [InlineData(110.0)]
    [InlineData(196.0)]
    public void Harmonic_rich_noisy_strings_give_no_octave_errors(double hz)
    {
        var detector = new YinPitchDetector(48_000);
        for (var seed = 1; seed <= 5; seed++)
        {
            var estimate = detector.Detect(Saw(hz, 48_000, detector.RequiredSamples, noise: 0.05, seed: seed));
            Assert.True(estimate.IsVoiced, $"seed {seed}: unvoiced");
            Assert.InRange(Cents(estimate.Hz, hz), -5, 5); // an octave error would be ±1200
        }
    }

    [Fact]
    public void Noise_and_silence_are_unvoiced()
    {
        var detector = new YinPitchDetector(48_000);
        var rng = new Random(7);
        var noise = new float[detector.RequiredSamples];
        for (var i = 0; i < noise.Length; i++) noise[i] = (float)(rng.NextDouble() * 2 - 1);

        Assert.False(detector.Detect(noise).IsVoiced);
        Assert.False(detector.Detect(new float[detector.RequiredSamples]).IsVoiced);
    }

    [Fact]
    public void Too_few_samples_is_unvoiced_and_extra_samples_use_the_most_recent()
    {
        var detector = new YinPitchDetector(48_000);
        Assert.False(detector.Detect(new float[detector.RequiredSamples - 1]).IsVoiced);

        // Older audio at 110 Hz followed by a full window at 220 Hz: the newest wins.
        var mixed = Sine(110, 48_000, 5_000).Concat(Sine(220, 48_000, detector.RequiredSamples)).ToArray();
        Assert.InRange(Cents(detector.Detect(mixed).Hz, 220), -1, 1);
    }

    [Fact]
    public void Detect_does_not_allocate()
    {
        var detector = new YinPitchDetector(48_000);
        var x = Sine(110, 48_000, detector.RequiredSamples);
        detector.Detect(x);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++) detector.Detect(x);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Theory]
    [InlineData("E2", 40)]
    [InlineData("A4", 69)]
    [InlineData("F#3", 54)]
    [InlineData("Bb1", 34)]
    [InlineData("B1", 35)]
    [InlineData("C♯4", 61)]
    [InlineData(" d3 ", 50)]
    public void Note_names_parse(string text, int midi)
    {
        Assert.True(Pitch.TryParseNoteName(text, out var parsed));
        Assert.Equal(midi, parsed);
        Assert.True(Pitch.TryParseNoteName(Pitch.NoteName(parsed), out var roundTrip)); // NoteName uses sharps
        Assert.Equal(midi, roundTrip);
    }

    [Theory]
    [InlineData("")]
    [InlineData("H2")]
    [InlineData("E")]
    [InlineData("E2x")]
    public void Bad_note_names_are_rejected(string text) => Assert.False(Pitch.TryParseNoteName(text, out _));
}
