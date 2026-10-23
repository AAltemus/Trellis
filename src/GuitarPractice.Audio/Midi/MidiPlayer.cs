using MeltySynth;

namespace GuitarPractice.Audio.Midi;

/// <summary>What <see cref="MidiPlayerEngine"/> plays through. Called only on the audio thread.</summary>
public interface IMidiSynth
{
    void Send(int channel, int command, int data1, int data2);

    /// <summary>Stereo render of the next <c>left.Length</c> samples.</summary>
    void Render(Span<float> left, Span<float> right);

    /// <summary>Silence every voice (immediately) and reset programs/controllers to their defaults.</summary>
    void Reset();
}

/// <summary>A MeltySynth synthesizer on a shared, lazily loaded SoundFont.</summary>
public sealed class SoundFontSynth : IMidiSynth
{
    private static readonly Dictionary<string, Lazy<SoundFont>> Fonts = [];
    private readonly Synthesizer _synth;

    public SoundFontSynth(string soundFontPath, int sampleRate)
    {
        _synth = new Synthesizer(Load(soundFontPath), new SynthesizerSettings(sampleRate) { EnableReverbAndChorus = true });
    }

    /// <summary>Loads (once per path, thread-safe) and keeps the SoundFont; ~1 s for a 30 MB file.</summary>
    public static SoundFont Load(string path)
    {
        Lazy<SoundFont> font;
        lock (Fonts)
        {
            if (!Fonts.TryGetValue(path, out font!))
                Fonts[path] = font = new Lazy<SoundFont>(() => new SoundFont(path));
        }
        return font.Value;
    }

    public void Send(int channel, int command, int data1, int data2) => _synth.ProcessMidiMessage(channel, command, data1, data2);

    public void Render(Span<float> left, Span<float> right) => _synth.Render(left, right);

    public void Reset() => _synth.Reset();
}

/// <summary>
/// Playback settings, swapped as a whole by the UI. <see cref="Speed"/>: 1 = as written (0.25–1.5; MIDI keeps its pitch).
/// Loop: bars <see cref="LoopFromBar"/>..<see cref="LoopToBar"/> inclusive (1-based). Count-in: one bar of side-stick
/// clicks before playback starts, and with <see cref="CountInEachLoop"/> before every pass of the loop too.
/// <see cref="Muted"/>/<see cref="Soloed"/>: bit n = <see cref="MidiSong.Tracks"/>[n] (first 64 tracks); any solo mutes
/// every track that isn't soloed. <see cref="TrackVolumes"/>[n]: 0–1 for <see cref="MidiSong.Tracks"/>[n] (missing = 1);
/// pass a new array to change it, never edit one the engine already has. <see cref="Click"/>: a metronome click on every
/// beat of the song (its tempo changes, time signatures and the speed), accented on each bar's first beat, at
/// <see cref="ClickVolume"/> (0–2, separate from <see cref="Volume"/>).
/// </summary>
public sealed record MidiPlayerSettings(
    double Speed = 1.0,
    bool LoopEnabled = false,
    int LoopFromBar = 1,
    int LoopToBar = 4,
    bool CountIn = false,
    float Volume = 1.0f,
    ulong Muted = 0,
    ulong Soloed = 0,
    bool CountInEachLoop = false,
    IReadOnlyList<float>? TrackVolumes = null,
    bool Click = false,
    float ClickVolume = 0.8f)
{
    public const double MinSpeed = 0.25;
    public const double MaxSpeed = 1.5;

    /// <summary>200 %: the soft limiter keeps it from clipping.</summary>
    public const float MaxVolume = 2f;

    public MidiPlayerSettings Normalized() => this with
    {
        Speed = double.IsFinite(Speed) ? Math.Clamp(Speed, MinSpeed, MaxSpeed) : 1.0,
        LoopFromBar = Math.Max(1, LoopFromBar),
        LoopToBar = Math.Max(Math.Max(1, LoopFromBar), LoopToBar),
        Volume = float.IsFinite(Volume) ? Math.Clamp(Volume, 0f, MaxVolume) : 1f,
        ClickVolume = float.IsFinite(ClickVolume) ? Math.Clamp(ClickVolume, 0f, MaxVolume) : 0.8f,
    };

    /// <summary>True when the track at <paramref name="trackSlot"/> (index into <see cref="MidiSong.Tracks"/>) is silenced.</summary>
    public bool IsSilenced(int trackSlot)
    {
        if (trackSlot is < 0 or >= 64) return false;
        var bit = 1UL << trackSlot;
        return Soloed != 0 ? (Soloed & bit) == 0 : (Muted & bit) != 0;
    }

    /// <summary>0–1 for the track at <paramref name="trackSlot"/>; 1 when not set.</summary>
    public float TrackVolume(int trackSlot)
    {
        if (TrackVolumes is not { } volumes || trackSlot < 0 || trackSlot >= volumes.Count) return 1f;
        var v = volumes[trackSlot];
        return float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 1f;
    }
}

/// <summary>
/// Plays a <see cref="MidiSong"/> through an <see cref="IMidiSynth"/>, sample-accurately, on the audio thread. The UI calls
/// <see cref="Load"/>, <see cref="Play"/>, <see cref="Pause"/>, <see cref="Seek"/> and sets <see cref="Settings"/>; those go
/// through a lock-free queue and take effect at the next block. Events are dispatched at block boundaries of
/// <see cref="BlockSize"/> samples (1.3 ms at 48 kHz). Seeking (and each loop restart) silences the synth, resets it and
/// replays every program/controller/pitch-bend change before the new position, so instruments are right mid-song.
/// Muting a track releases the notes it's holding. A track's volume scales the channel volume (CC 7) of the channels it
/// plays on, including the file's own CC 7 changes; a channel shared by several tracks follows the quietest of them, and
/// the drum channel is at the file's level while the count-in clicks. <see cref="Render"/> doesn't allocate, lock or block.
/// </summary>
public sealed class MidiPlayerEngine
{
    public const int BlockSize = 64;
    public const int CountInNote = 37;       // GM side stick
    public const int CountInAccentNote = 76; // GM high woodblock (beat 1)

    /// <summary>CC 7 after a reset (General MIDI default, and MeltySynth's).</summary>
    public const int DefaultChannelVolume = 100;

    /// <summary>
    /// L+R → mono gain at 100 % volume. MeltySynth's output is quiet: a dense band mix (distorted guitar, bass, drums)
    /// peaked at −10 dBFS with the old 0.5; 1.6 puts it near full scale (measured 2026-10-18; user: "needs to be a bit louder").
    /// </summary>
    public const float MixGain = 1.6f;

    /// <summary>Linear up to 0.7, then a smooth knee that never exceeds 1: only the loudest peaks are shaped.</summary>
    public static float SoftLimit(float x)
    {
        const float Knee = 0.7f;
        var magnitude = MathF.Abs(x);
        if (magnitude <= Knee) return x;
        var limited = Knee + (1 - Knee) * MathF.Tanh((magnitude - Knee) / (1 - Knee));
        return x < 0 ? -limited : limited;
    }

    private readonly IMidiSynth _synth;
    private readonly SpscRing<Command> _commands = new(64);
    private readonly float[] _left = new float[BlockSize];
    private readonly float[] _right = new float[BlockSize];
    private readonly sbyte[] _noteOwner = new sbyte[16 * 128]; // track slot holding (channel, note), -1 = none
    private readonly int[] _trackSlot = new int[256];           // file track index → slot in Tracks (or -1)
    private readonly ulong[] _channelTracks = new ulong[16];    // track slots (bits) that play on each channel
    private readonly int[] _fileVolume = new int[16];           // the file's CC 7 per channel (synth default 100)
    private readonly int[] _sentVolume = new int[16];           // CC 7 last sent per channel

    private MidiPlayerSettings _settings = new();
    private MidiSong? _pendingSong;

    // Audio-thread state.
    private MidiSong? _song;
    private bool _playing;
    private double _position;  // song seconds
    private int _next;         // index of the next event to dispatch
    private ulong _silenced;   // tracks muted/not-soloed under the current settings
    private int _countInTotal;      // beats in the count-in bar (0 = not counting in)
    private int _countInBeat;       // next beat to click
    private long _countInStart;     // sample of beat 1
    private long _countInEnd;       // sample where the song starts
    private double _countInBeatSamples;
    private long _countInOffSample = -1;
    private long _sample;      // samples rendered since construction

    // Click: the song's next beat (kept up to date whether or not the click is on, so turning it on mid-song just works)
    // and the click sounding now, from sample _clickStart (absolute).
    private readonly float[] _accentClick;
    private readonly float[] _beatClick;
    private long _nextBeatTick = long.MaxValue;
    private double _nextBeatSeconds = double.MaxValue;
    private bool _nextBeatIsDownbeat;
    private float[]? _click;
    private long _clickStart;

    private enum CommandKind { Load, Play, Pause, Seek }

    private readonly record struct Command(CommandKind Kind, double Value);

    public MidiPlayerEngine(int sampleRate, IMidiSynth synth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        ArgumentNullException.ThrowIfNull(synth);
        SampleRate = sampleRate;
        _synth = synth;
        Array.Fill(_noteOwner, (sbyte)-1);
        Array.Fill(_fileVolume, DefaultChannelVolume);
        Array.Fill(_sentVolume, DefaultChannelVolume);
        _accentClick = ClickSynth.Click(sampleRate, 1500, 1.0f); // the Metronome's sounds
        _beatClick = ClickSynth.Click(sampleRate, 1000, 0.9f);
    }

    public int SampleRate { get; }

    public MidiPlayerSettings Settings
    {
        get => Volatile.Read(ref _settings);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _settings, value.Normalized());
        }
    }

    // ---- Read by the UI ----

    private double _reportedPosition;
    private int _reportedState; // 0 stopped/paused, 1 playing, 2 counting in
    private int _reportedCountInBeat;

    /// <summary>Song seconds (100 % speed) of the audio rendered last.</summary>
    public double Position => Volatile.Read(ref _reportedPosition);

    public bool IsPlaying => Volatile.Read(ref _reportedState) != 0;

    public bool IsCountingIn => Volatile.Read(ref _reportedState) == 2;

    /// <summary>While counting in: the beat clicking now (1-based); 0 otherwise.</summary>
    public int CountInBeat => Volatile.Read(ref _reportedCountInBeat);

    // ---- Commands from the UI ----

    /// <summary>Replaces the song (stops, back to the start).</summary>
    public void Load(MidiSong? song)
    {
        Volatile.Write(ref _pendingSong, song);
        _commands.TryEnqueue(new Command(CommandKind.Load, 0));
    }

    /// <summary>Plays from the current position (after one bar of count-in clicks when <see cref="MidiPlayerSettings.CountIn"/>).</summary>
    public void Play() => _commands.TryEnqueue(new Command(CommandKind.Play, 0));

    public void Pause() => _commands.TryEnqueue(new Command(CommandKind.Pause, 0));

    public void Seek(double seconds) => _commands.TryEnqueue(new Command(CommandKind.Seek, seconds));

    // ---- Audio thread ----

    /// <summary>Fills <paramref name="output"/> (mono). Any buffer size.</summary>
    public void Render(Span<float> output)
    {
        while (_commands.TryDequeue(out var command)) Apply(command);

        var settings = Volatile.Read(ref _settings);
        UpdateSilenced(settings);

        var done = 0;
        while (done < output.Length)
        {
            var n = Math.Min(BlockSize, output.Length - done);
            UpdateChannelVolumes(settings);
            if (_playing && _song is { } song)
            {
                if (CountingIn) CountIn(n);
                else Advance(song, settings, n);
            }
            if (_countInOffSample >= 0 && _sample >= _countInOffSample)
            {
                _synth.Send(MidiSong.DrumChannel, 0x80, CountInNote, 0);
                _synth.Send(MidiSong.DrumChannel, 0x80, CountInAccentNote, 0);
                _countInOffSample = -1;
            }

            var left = _left.AsSpan(0, n);
            var right = _right.AsSpan(0, n);
            _synth.Render(left, right);
            var gain = settings.Volume * MixGain;
            for (var i = 0; i < n; i++) output[done + i] = SoftLimit((left[i] + right[i]) * gain + ClickSample(_sample + i, settings));
            done += n;
            _sample += n;
        }

        Volatile.Write(ref _reportedPosition, _position);
        Volatile.Write(ref _reportedState, !_playing ? 0 : CountingIn ? 2 : 1);
        Volatile.Write(ref _reportedCountInBeat, _playing && CountingIn
            ? Math.Clamp((int)((_sample - _countInStart) / _countInBeatSamples) + 1, 1, _countInTotal)
            : 0);
    }

    private void Apply(Command command)
    {
        switch (command.Kind)
        {
            case CommandKind.Load:
                _song = Volatile.Read(ref _pendingSong);
                _playing = false;
                _countInTotal = 0;
                Array.Fill(_trackSlot, -1);
                Array.Clear(_channelTracks);
                if (_song is { } loaded)
                    for (var slot = 0; slot < loaded.Tracks.Count; slot++)
                    {
                        var track = loaded.Tracks[slot];
                        if (track.Index < _trackSlot.Length) _trackSlot[track.Index] = slot;
                        if (slot >= 64) continue;
                        for (var c = 0; c < track.Channels.Count; c++) _channelTracks[track.Channels[c] & 0x0F] |= 1UL << slot;
                    }
                Chase(0);
                break;
            case CommandKind.Play when _song is not null && !_playing:
                var settings = Volatile.Read(ref _settings);
                if (_position >= _song.LengthSeconds) Chase(settings.LoopEnabled ? _song.BarStartSeconds(settings.LoopFromBar) : 0);
                _playing = true;
                if (settings.CountIn) StartCountIn(_song, settings);
                break;
            case CommandKind.Pause:
                _playing = false;
                _countInTotal = 0;
                _click = null;
                ReleaseAll();
                break;
            case CommandKind.Seek when _song is not null:
                _countInTotal = 0;
                Chase(Math.Clamp(command.Value, 0, _song.LengthSeconds));
                break;
        }
    }

    private void Advance(MidiSong song, MidiPlayerSettings settings, int samples)
    {
        var end = _position + samples / (double)SampleRate * settings.Speed;
        var loopEnd = settings.LoopEnabled ? song.BarEndSeconds(settings.LoopToBar) : double.MaxValue;
        ClickBeats(song, settings, Math.Min(end, loopEnd));
        if (end >= loopEnd)
        {
            Dispatch(song, loopEnd);
            Chase(song.BarStartSeconds(settings.LoopFromBar));
            if (settings.CountIn && settings.CountInEachLoop) StartCountIn(song, settings);
            return;
        }

        Dispatch(song, end);
        _position = end;
        if (_position >= song.LengthSeconds)
        {
            _playing = false;
            ReleaseAll();
        }
    }

    /// <summary>Sends every event before <paramref name="until"/> (song seconds); note-ons of silenced tracks are skipped.</summary>
    private void Dispatch(MidiSong song, double until)
    {
        var events = song.Events;
        var times = song.EventSeconds;
        while (_next < events.Count && times[_next] < until)
        {
            var e = events[_next++];
            var slot = e.Track < _trackSlot.Length ? _trackSlot[e.Track] : -1;
            var key = e.Channel * 128 + e.Data1;
            if (e.IsNoteOn)
            {
                if (slot >= 0 && slot < 64 && (_silenced & (1UL << slot)) != 0) continue;
                _noteOwner[key] = (sbyte)Math.Min(slot, 63);
            }
            else if (e.IsNoteOff)
            {
                _noteOwner[key] = -1;
            }
            else if (IsChannelVolume(e))
            {
                SendFileVolume(e.Channel, e.Data2);
                continue;
            }
            _synth.Send(e.Channel, e.Command, e.Data1, e.Data2);
        }
    }

    private static bool IsChannelVolume(MidiEvent e) => e.Command == 0xB0 && e.Data1 == 7;

    /// <summary>The file set a channel's volume: send it scaled by the track volume.</summary>
    private void SendFileVolume(int channel, int value)
    {
        _fileVolume[channel] = value;
        var scaled = ScaledVolume(channel, Volatile.Read(ref _settings));
        _synth.Send(channel, 0xB0, 7, scaled);
        _sentVolume[channel] = scaled;
    }

    private int ScaledVolume(int channel, MidiPlayerSettings settings)
    {
        if (channel == MidiSong.DrumChannel && CountingIn) return _fileVolume[channel];
        var gain = 1f;
        var tracks = _channelTracks[channel];
        for (var slot = 0; tracks != 0; slot++, tracks >>= 1)
            if ((tracks & 1) != 0) gain = Math.Min(gain, settings.TrackVolume(slot));
        return Math.Clamp((int)MathF.Round(_fileVolume[channel] * gain), 0, 127);
    }

    /// <summary>Sends CC 7 where a track volume (or the count-in) changed what a channel should be at.</summary>
    private void UpdateChannelVolumes(MidiPlayerSettings settings)
    {
        for (var channel = 0; channel < 16; channel++)
        {
            var scaled = ScaledVolume(channel, settings);
            if (scaled == _sentVolume[channel]) continue;
            _synth.Send(channel, 0xB0, 7, scaled);
            _sentVolume[channel] = scaled;
        }
    }

    /// <summary>Jumps to <paramref name="seconds"/>: silence, reset, replay the state (not the notes) before it.</summary>
    private void Chase(double seconds)
    {
        _synth.Reset();
        Array.Fill(_noteOwner, (sbyte)-1);
        Array.Fill(_fileVolume, DefaultChannelVolume);
        Array.Fill(_sentVolume, DefaultChannelVolume);
        _position = seconds;
        _next = 0;
        _nextBeatTick = long.MaxValue;
        _nextBeatSeconds = double.MaxValue;
        if (_song is not { } song) return;
        // Exactly on a beat (bar starts: Play from the start, loop restarts) counts, so the downbeat clicks.
        var firstTick = song.TickAt(seconds);
        if (song.SecondsAt(firstTick) < seconds) firstTick++;
        SetNextBeat(song, song.BeatAtOrAfter(firstTick));
        var events = song.Events;
        var times = song.EventSeconds;
        while (_next < events.Count && times[_next] < seconds)
        {
            var e = events[_next++];
            if (IsChannelVolume(e)) SendFileVolume(e.Channel, e.Data2);
            else if (e.Command is 0xB0 or 0xC0 or 0xE0) _synth.Send(e.Channel, e.Command, e.Data1, e.Data2);
        }
    }

    private void UpdateSilenced(MidiPlayerSettings settings)
    {
        ulong silenced = 0;
        var count = Math.Min(64, _song?.Tracks.Count ?? 0);
        for (var slot = 0; slot < count; slot++)
            if (settings.IsSilenced(slot)) silenced |= 1UL << slot;
        var newlySilenced = silenced & ~_silenced;
        _silenced = silenced;
        if (newlySilenced == 0) return;

        // Release whatever the newly silenced tracks are holding.
        for (var key = 0; key < _noteOwner.Length; key++)
        {
            var owner = _noteOwner[key];
            if (owner < 0 || (newlySilenced & (1UL << owner)) == 0) continue;
            _synth.Send(key / 128, 0x80, key % 128, 0);
            _noteOwner[key] = -1;
        }
    }

    private void ReleaseAll()
    {
        for (var channel = 0; channel < 16; channel++) _synth.Send(channel, 0xB0, 123, 0); // all notes off (release tails)
        Array.Fill(_noteOwner, (sbyte)-1);
    }

    private void SetNextBeat(MidiSong song, (long Tick, bool Downbeat) beat)
    {
        _nextBeatTick = beat.Tick;
        _nextBeatIsDownbeat = beat.Downbeat;
        _nextBeatSeconds = beat.Tick == long.MaxValue ? double.MaxValue : song.SecondsAt(beat.Tick);
    }

    /// <summary>
    /// Steps over the song's beats before <paramref name="until"/> (song seconds); with the click on, each one starts a
    /// click at its exact sample in this block (song seconds → samples through the speed).
    /// </summary>
    private void ClickBeats(MidiSong song, MidiPlayerSettings settings, double until)
    {
        while (_nextBeatSeconds < until)
        {
            if (settings.Click)
            {
                _click = _nextBeatIsDownbeat ? _accentClick : _beatClick;
                _clickStart = _sample + Math.Max(0, (long)Math.Round((_nextBeatSeconds - _position) / settings.Speed * SampleRate));
            }
            SetNextBeat(song, song.BeatAtOrAfter(_nextBeatTick + 1));
        }
    }

    /// <summary>The click's sample at absolute sample <paramref name="sample"/> (0 when none is sounding).</summary>
    private float ClickSample(long sample, MidiPlayerSettings settings)
    {
        if (_click is not { } click || sample < _clickStart) return 0f;
        var i = sample - _clickStart;
        if (i < click.Length) return settings.Click ? click[i] * settings.ClickVolume : 0f;
        _click = null;
        return 0f;
    }

    private bool CountingIn => _countInTotal > 0 && _sample < _countInEnd;

    /// <summary>One bar of clicks at the tempo, time signature and speed at the current position; the song follows it.</summary>
    private void StartCountIn(MidiSong song, MidiPlayerSettings settings)
    {
        var sig = song.TimeSignatureAt(song.TickAt(_position));
        var beatSeconds = 60.0 / song.QuarterBpmAt(_position) * 4.0 / sig.Denominator / settings.Speed;
        _countInBeatSamples = beatSeconds * SampleRate;
        _countInTotal = sig.Numerator;
        _countInBeat = 0;
        _countInStart = _sample;
        _countInEnd = _sample + (long)Math.Round(sig.Numerator * _countInBeatSamples);
    }

    /// <summary>Clicks the beats that fall in this block (beat 1 accented); each click lasts half a beat.</summary>
    private void CountIn(int samples)
    {
        while (_countInBeat < _countInTotal && _countInStart + (long)Math.Round(_countInBeat * _countInBeatSamples) < _sample + samples)
        {
            var accent = _countInBeat == 0;
            _synth.Send(MidiSong.DrumChannel, 0x90, accent ? CountInAccentNote : CountInNote, accent ? 110 : 90);
            _countInOffSample = _sample + (long)(_countInBeatSamples / 2);
            _countInBeat++;
        }
    }
}
