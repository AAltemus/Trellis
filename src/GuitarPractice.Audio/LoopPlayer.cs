namespace GuitarPractice.Audio;

/// <summary>
/// Playback options, swapped atomically by the UI. Loop points are in seconds of the take; a loop shorter than
/// <see cref="MinimumLoop"/> is ignored. Count-in plays <see cref="CountInBeats"/> clicks at <see cref="CountInBpm"/>
/// before playback starts (from the loop start when looping). Volume above 1 is soft-limited, like the metronome.
/// </summary>
public sealed record PlayerSettings(
    float Volume = 1f,
    bool LoopEnabled = false,
    double LoopStart = 0,
    double LoopEnd = 0,
    bool CountIn = false,
    double CountInBpm = 100,
    int CountInBeats = 4)
{
    public static readonly TimeSpan MinimumLoop = TimeSpan.FromMilliseconds(100);

    public PlayerSettings Normalized() => this with
    {
        Volume = float.IsFinite(Volume) ? Math.Clamp(Volume, 0f, MetronomeSettings.MaxVolume) : 1f,
        LoopStart = double.IsFinite(LoopStart) ? Math.Max(0, Math.Min(LoopStart, LoopEnd)) : 0,
        LoopEnd = double.IsFinite(LoopEnd) ? Math.Max(0, Math.Max(LoopStart, LoopEnd)) : 0,
        CountInBpm = double.IsFinite(CountInBpm) ? Math.Clamp(CountInBpm, MetronomeSettings.MinBpm, MetronomeSettings.MaxBpm) : 100,
        CountInBeats = Math.Clamp(CountInBeats, 1, 8),
    };
}

/// <summary>
/// Plays an <see cref="AudioTake"/> from the output render callback: play/pause/seek, optional A–B loop with an
/// equal-power crossfade across the seam (no click, no gap, sample-accurate), optional metronome count-in, and linear
/// resampling when the take's rate differs from the output's. <see cref="Render"/> doesn't allocate, lock or block;
/// control calls are lock-free requests the audio thread picks up at the next buffer.
/// </summary>
public sealed class LoopPlayer
{
    public static readonly TimeSpan SeamCrossfade = TimeSpan.FromMilliseconds(8);

    private readonly int _outputRate;
    private readonly float[] _accentClick;
    private readonly float[] _beatClick;
    private AudioTake? _take;
    private PlayerSettings _settings = new();

    // Requests from the UI thread.
    private long _seekRequestBits = BitConverter.DoubleToInt64Bits(double.NaN);
    private int _playRequest;   // 1 = play (with count-in if set), 2 = pause
    private volatile bool _playing;
    private volatile bool _countingIn;

    // Audio-thread state.
    private AudioTake? _renderTake;
    private double _position;   // in take samples
    private double _countInPos; // in output samples
    private float[]? _click;
    private int _clickPos;
    private long _positionBits; // published position (seconds) for the UI
    private int _loopCount;

    public LoopPlayer(int outputRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(outputRate, 8_000);
        _outputRate = outputRate;
        _accentClick = ClickSynth.Click(outputRate, 1500, 1.0f);
        _beatClick = ClickSynth.Click(outputRate, 1000, 0.9f);
    }

    public int OutputRate => _outputRate;
    public AudioTake? Take => Volatile.Read(ref _take);
    public bool IsPlaying => _playing;
    public bool IsCountingIn => _countingIn;

    /// <summary>Times the loop has wrapped since the last Play.</summary>
    public int LoopCount => Volatile.Read(ref _loopCount);

    /// <summary>Current playback position in seconds of the take (updated every buffer).</summary>
    public double PositionSeconds => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _positionBits));

    public PlayerSettings Settings
    {
        get => Volatile.Read(ref _settings);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _settings, value.Normalized());
        }
    }

    /// <summary>Switches to another take (stops playback, position 0).</summary>
    public void Load(AudioTake? take)
    {
        _playing = false;
        _countingIn = false;
        Volatile.Write(ref _take, take);
        Seek(0);
    }

    public void Play()
    {
        if (Take is null) return;
        Interlocked.Exchange(ref _playRequest, 1);
        _playing = true;
    }

    public void Pause()
    {
        Interlocked.Exchange(ref _playRequest, 2);
        _playing = false;
        _countingIn = false;
    }

    public void Seek(double seconds)
    {
        Interlocked.Exchange(ref _seekRequestBits, BitConverter.DoubleToInt64Bits(Math.Max(0, seconds)));
        Interlocked.Exchange(ref _positionBits, BitConverter.DoubleToInt64Bits(Math.Max(0, seconds)));
    }

    /// <summary>Audio thread: fills <paramref name="output"/> (mono).</summary>
    public void Render(Span<float> output)
    {
        var take = Volatile.Read(ref _take);
        if (!ReferenceEquals(take, _renderTake))
        {
            _renderTake = take;
            _position = 0;
        }

        var seek = BitConverter.Int64BitsToDouble(Interlocked.Exchange(ref _seekRequestBits, BitConverter.DoubleToInt64Bits(double.NaN)));
        if (!double.IsNaN(seek) && take is not null) _position = Math.Min(seek * take.SampleRate, take.Samples.Length);

        var s = Volatile.Read(ref _settings);
        switch (Interlocked.Exchange(ref _playRequest, 0))
        {
            case 1 when take is not null:
                _loopCount = 0;
                var (loopStart, loopEnd, _) = LoopBounds(s, take);
                if (loopEnd > 0 && (_position < loopStart || _position >= loopEnd)) _position = loopStart;
                if (_position >= take.Samples.Length) _position = 0; // play again from the top after the end
                _countingIn = s.CountIn;
                _countInPos = 0;
                _click = null;
                break;
        }

        if (!_playing || take is null)
        {
            output.Clear();
            Publish(take);
            return;
        }

        var gain = s.Volume;
        var softClipNorm = gain > 1f ? 1f / MathF.Tanh(gain) : 0f;
        var step = take.SampleRate / (double)_outputRate;
        var (a, b, fade) = LoopBounds(s, take);
        var beatLength = _outputRate * 60.0 / s.CountInBpm;

        for (var i = 0; i < output.Length; i++)
        {
            float value;
            if (_countingIn)
            {
                // Click k at exactly k·beatLength output samples (same grid idea as the metronome).
                var beat = (int)Math.Floor(_countInPos / beatLength + 1e-9);
                if (beat >= s.CountInBeats)
                {
                    _countingIn = false;
                    value = 0;
                }
                else
                {
                    if (_countInPos - beat * beatLength < 1)
                    {
                        _click = beat == 0 ? _accentClick : _beatClick;
                        _clickPos = 0;
                    }
                    value = 0;
                    if (_click is { } click)
                    {
                        value = click[_clickPos++];
                        if (_clickPos == click.Length) _click = null;
                    }
                    _countInPos++;
                    output[i] = value * Math.Min(gain, 1f);
                    continue;
                }
            }

            if (b > 0 && _position >= b)
            {
                _position = a + (_position - a) % (b - a); // seeked past the end of the loop: wrap without fade
            }

            if (b > 0 && _position >= b - fade)
            {
                // Equal-power crossfade: the loop's tail [B−fade, B) fades out while the audio just before A,
                // [A−fade, A), fades in; at B playback continues at exactly A. Each cycle is therefore exactly B − A
                // long (no drift against a metronome) and the seam is continuous. Before the start of the take the
                // fade-in source is silence.
                var t = (_position - (b - fade)) / fade;
                var theta = t * Math.PI / 2;
                var lead = a + (_position - b);
                var incoming = lead >= 0 ? Sample(take, lead) : 0f;
                value = (float)(Sample(take, _position) * Math.Cos(theta) + incoming * Math.Sin(theta));
                _position += step;
                if (_position >= b)
                {
                    _position = a + (_position - b);
                    _loopCount++;
                }
            }
            else if (_position < take.Samples.Length)
            {
                value = Sample(take, _position);
                _position += step;
            }
            else
            {
                _playing = false; // reached the end (not looping)
                output[i..].Clear();
                break;
            }

            value *= gain;
            if (softClipNorm > 0) value = MathF.Tanh(value) * softClipNorm;
            output[i] = value;
        }

        Publish(take);
    }

    private void Publish(AudioTake? take)
    {
        var seconds = take is null ? 0 : _position / take.SampleRate;
        Interlocked.Exchange(ref _positionBits, BitConverter.DoubleToInt64Bits(seconds));
    }

    /// <summary>Loop start/end in take samples and the seam crossfade length; (0, 0, 0) when not looping.</summary>
    private static (double Start, double End, double Fade) LoopBounds(PlayerSettings s, AudioTake take)
    {
        if (!s.LoopEnabled) return (0, 0, 0);
        var start = Math.Clamp(s.LoopStart * take.SampleRate, 0, take.Samples.Length);
        var end = Math.Clamp(s.LoopEnd * take.SampleRate, 0, take.Samples.Length);
        if (end - start < PlayerSettings.MinimumLoop.TotalSeconds * take.SampleRate) return (0, 0, 0);
        var fade = Math.Min(SeamCrossfade.TotalSeconds * take.SampleRate, (end - start) / 4);
        return (start, end, fade);
    }

    private static float Sample(AudioTake take, double position)
    {
        var samples = take.Samples;
        var i = (int)position;
        if (i < 0) return samples.Length > 0 ? samples[0] : 0;
        if (i >= samples.Length - 1) return samples.Length > 0 ? samples[^1] : 0;
        var frac = (float)(position - i);
        return samples[i] + (samples[i + 1] - samples[i]) * frac;
    }
}
