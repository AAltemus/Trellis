namespace GuitarPractice.Audio.Midi;

/// <summary>
/// How loud a song comes out of the synth, so every file can play at about the same level. MeltySynth's output level
/// depends on how much is playing: the user's one-instrument riffs peaked 14–16 dB below a four-track band file at the
/// same settings (measured 2026-11-25: band −1.0 dBFS peak / −15.7 dB loudest-second RMS; riffs −14.7…−15.7 dBFS /
/// −27…−29 dB), so with one fixed gain the riffs stayed quiet even at 200 %.
/// </summary>
public static class MidiLevel
{
    /// <summary>The loudest second of a song is brought to this RMS level (the band file's level at 100 %).</summary>
    public const double TargetDb = -16;

    /// <summary>At most +18 dB of boost (a near-silent file isn't blown up) and −6 dB of cut.</summary>
    public const float MinGain = 0.5f;
    public const float MaxGain = 8f;

    /// <summary>Below this, nothing is really playing: leave the gain at 1.</summary>
    private const double SilenceDb = -70;

    /// <summary>How much of a song is measured: all of a short riff, the start of a long routine.</summary>
    public const double MeasureSeconds = 30;

    /// <summary>
    /// Renders up to <see cref="MeasureSeconds"/> of <paramref name="song"/> offline on <paramref name="synth"/> (a
    /// synth of its own: never the one that's playing) at the default settings and returns the gain that brings its
    /// loudest second to <see cref="TargetDb"/>. Null when <paramref name="cancel"/> is set before it's done (no
    /// exception: the caller just moved on to another file). Takes about 1/12 of the measured time on a desktop CPU.
    /// </summary>
    public static float? SongGain(MidiSong song, IMidiSynth synth, int sampleRate, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(synth);
        var engine = new MidiPlayerEngine(sampleRate, synth) { Settings = new MidiPlayerSettings() };
        engine.Load(song);
        engine.Play();
        var total = (long)(Math.Min(song.LengthSeconds, MeasureSeconds) * sampleRate);
        var block = new float[1024];
        double windowSum = 0, loudest = 0;
        long windowCount = 0;
        for (long done = 0; done < total; done += block.Length)
        {
            if (cancel.IsCancellationRequested) return null;
            engine.Render(block);
            foreach (var x in block) windowSum += x * x;
            windowCount += block.Length;
            if (windowCount >= sampleRate || done + block.Length >= total)
            {
                loudest = Math.Max(loudest, Math.Sqrt(windowSum / windowCount));
                windowSum = 0;
                windowCount = 0;
            }
        }
        return GainFor(loudest);
    }

    /// <summary>The gain that moves a loudest-second RMS of <paramref name="loudestRms"/> to <see cref="TargetDb"/>.</summary>
    public static float GainFor(double loudestRms)
    {
        var db = 20 * Math.Log10(Math.Max(loudestRms, 1e-9));
        if (db < SilenceDb) return 1f;
        return Math.Clamp((float)Math.Pow(10, (TargetDb - db) / 20), MinGain, MaxGain);
    }
}
