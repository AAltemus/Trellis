using GuitarPractice.Audio.Midi;

namespace GuitarPractice.Audio.Tests;

/// <summary>Builds standard MIDI files byte by byte for the tests.</summary>
internal sealed class Smf(int ticksPerQuarter = 480, int format = 1)
{
    private readonly List<byte[]> _tracks = [];

    public Smf Track(params (long Delta, byte[] Bytes)[] events)
    {
        var data = new List<byte>();
        foreach (var (delta, bytes) in events)
        {
            data.AddRange(VarLen(delta));
            data.AddRange(bytes);
        }
        data.AddRange([0x00, 0xFF, 0x2F, 0x00]);
        _tracks.Add([.. data]);
        return this;
    }

    public byte[] Build()
    {
        var file = new List<byte>();
        file.AddRange("MThd"u8.ToArray());
        file.AddRange(BigEndian(6, 4));
        file.AddRange(BigEndian(format, 2));
        file.AddRange(BigEndian(_tracks.Count, 2));
        file.AddRange(BigEndian(ticksPerQuarter, 2));
        foreach (var t in _tracks)
        {
            file.AddRange("MTrk"u8.ToArray());
            file.AddRange(BigEndian(t.Length, 4));
            file.AddRange(t);
        }
        return [.. file];
    }

    public static byte[] Name(string name) => [0xFF, 0x03, (byte)name.Length, .. System.Text.Encoding.ASCII.GetBytes(name)];
    public static byte[] Tempo(double bpm)
    {
        var micro = (int)Math.Round(60_000_000 / bpm);
        return [0xFF, 0x51, 0x03, (byte)(micro >> 16), (byte)(micro >> 8), (byte)micro];
    }
    public static byte[] TimeSig(int numerator, int denominator) => [0xFF, 0x58, 0x04, (byte)numerator, (byte)Math.Log2(denominator), 24, 8];
    public static byte[] On(int channel, int note, int velocity = 100) => [(byte)(0x90 | channel), (byte)note, (byte)velocity];
    public static byte[] Off(int channel, int note) => [(byte)(0x80 | channel), (byte)note, 0];
    public static byte[] Program(int channel, int program) => [(byte)(0xC0 | channel), (byte)program];
    public static byte[] Control(int channel, int controller, int value) => [(byte)(0xB0 | channel), (byte)controller, (byte)value];

    private static byte[] BigEndian(int value, int bytes) => Enumerable.Range(0, bytes).Select(i => (byte)(value >> (8 * (bytes - 1 - i)))).ToArray();

    private static byte[] VarLen(long value)
    {
        var stack = new Stack<byte>();
        stack.Push((byte)(value & 0x7F));
        while ((value >>= 7) > 0) stack.Push((byte)((value & 0x7F) | 0x80));
        return [.. stack];
    }
}

/// <summary>Does nothing; for allocation tests.</summary>
internal sealed class SilentSynth : IMidiSynth
{
    public void Send(int channel, int command, int data1, int data2) { }
    public void Render(Span<float> left, Span<float> right) { left.Clear(); right.Clear(); }
    public void Reset() { }
}

/// <summary>Records what the engine sends and at which output sample.</summary>
internal sealed class FakeSynth : IMidiSynth
{
    private long _rendered;

    public List<(long Sample, int Channel, int Command, int Data1, int Data2)> Sent { get; } = [];
    public List<long> Resets { get; } = [];

    public IEnumerable<(long Sample, int Note)> NoteOns(int channel) =>
        Sent.Where(s => s.Channel == channel && s.Command == 0x90 && s.Data2 > 0).Select(s => (s.Sample, s.Data1));

    public void Send(int channel, int command, int data1, int data2) => Sent.Add((_rendered, channel, command, data1, data2));

    public void Render(Span<float> left, Span<float> right)
    {
        left.Clear();
        right.Clear();
        _rendered += left.Length;
    }

    public void Reset() => Resets.Add(_rendered);
}

public class MidiTests
{
    private const int Rate = 48_000;
    private const int Q = 480; // ticks per quarter

    /// <summary>
    /// Conductor: 120 BPM 4/4, 60 BPM from bar 3, 3/4 from bar 5. Guitar (ch 0, distortion) plays a quarter note at the
    /// start of every bar for 6 bars; drums (ch 9) a kick on beat 1 of bar 1 and 2; an empty "Notes" track.
    /// </summary>
    private static byte[] Song()
    {
        var conductor = new List<(long, byte[])>
        {
            (0, Smf.Name("Song")), (0, Smf.TimeSig(4, 4)), (0, Smf.Tempo(120)),
            (8 * Q, Smf.Tempo(60)),          // bar 3
            (8 * Q, Smf.TimeSig(3, 4)),      // bar 5
        };
        var guitar = new List<(long, byte[])> { (0, Smf.Name("Lead")), (0, Smf.Program(0, 30)), (0, Smf.Control(0, 7, 100)) };
        long[] barStarts = [0, 4 * Q, 8 * Q, 12 * Q, 16 * Q, 19 * Q];
        long last = 0;
        foreach (var start in barStarts)
        {
            guitar.Add((start - last, Smf.On(0, 64)));
            guitar.Add((Q, Smf.Off(0, 64)));
            last = start + Q;
        }
        return new Smf(Q)
            .Track([.. conductor])
            .Track([.. guitar])
            .Track((0, Smf.Name("Drums")), (0, Smf.On(9, 36)), (Q, Smf.Off(9, 36)), (3 * Q, Smf.On(9, 36)), (Q, Smf.Off(9, 36)))
            .Track((0, Smf.Name("Notes")))
            .Build();
    }

    private static MidiPlayerEngine Engine(MidiSong song, IMidiSynth synth, MidiPlayerSettings? settings = null)
    {
        var engine = new MidiPlayerEngine(Rate, synth);
        if (settings is not null) engine.Settings = settings;
        engine.Load(song);
        return engine;
    }

    private static void Render(MidiPlayerEngine engine, double seconds)
    {
        var buffer = new float[256];
        for (var done = 0; done < seconds * Rate; done += buffer.Length) engine.Render(buffer);
    }

    // ---- Reading ----

    [Fact]
    public void Reads_tracks_instruments_tempo_map_and_bars()
    {
        var song = MidiFileReader.Read(Song());

        Assert.Equal(["Lead", "Drums"], song.Tracks.Select(t => t.Name));                  // the empty track is left out
        Assert.Equal(("Distortion Guitar", false), (song.Tracks[0].Instrument, song.Tracks[0].IsDrums));
        Assert.Equal(("Drums", true, 2), (song.Tracks[1].Instrument, song.Tracks[1].IsDrums, song.Tracks[1].NoteCount));

        Assert.Equal(0.0, song.SecondsAt(0));
        Assert.Equal(4.0, song.SecondsAt(8 * Q), 9);         // 2 bars of 4/4 at 120
        Assert.Equal(12.0, song.SecondsAt(16 * Q), 9);       // + 2 bars at 60
        Assert.Equal(8 * Q, song.TickAt(4.0));
        Assert.Equal(60, song.QuarterBpmAt(5), 6);

        // Bars: 4/4 ×4, then 3/4: starts at 0, 4Q, 8Q, 12Q, 16Q, 19Q; the last note-off (20Q) is inside bar 6.
        Assert.Equal(6, song.BarCount);
        Assert.Equal([0L, 4 * Q, 8 * Q, 12 * Q, 16 * Q, 19 * Q], Enumerable.Range(1, 6).Select(song.BarStartTick));
        Assert.Equal(22 * Q, song.BarEndTick(6));
        Assert.Equal(song.SecondsAt(22 * Q), song.LengthSeconds, 9);
        Assert.Equal((5, 6), (song.BarAt(song.BarStartSeconds(5) + 0.1), song.BarAt(song.LengthSeconds)));
        Assert.Equal((3, 4), (song.TimeSignatureAt(17 * Q).Numerator, song.TimeSignatureAt(17 * Q).Denominator));
    }

    [Fact]
    public void Running_status_and_zero_velocity_note_offs_are_understood()
    {
        // On(60) then, with running status, On(62), then 60 and 62 "on" with velocity 0 (= off).
        var data = new Smf(Q, format: 0).Track(
            (0, [0x90, 60, 100]), (0, [62, 100]), (Q, [60, 0]), (0, [62, 0])).Build();

        var song = MidiFileReader.Read(data);

        Assert.Equal(4, song.Events.Count);
        Assert.Equal([true, true, false, false], song.Events.Select(e => e.IsNoteOn));
        Assert.All(song.Events.Skip(2), e => Assert.True(e.IsNoteOff));
        Assert.Equal((120.0, 4, 4), (song.QuarterBpmAt(0), song.TimeSignatures[0].Numerator, song.TimeSignatures[0].Denominator)); // defaults
        Assert.Equal("Acoustic Grand Piano", song.Tracks.Single().Instrument);
    }

    [Fact]
    public void Bad_files_say_why()
    {
        Assert.Contains("Not a MIDI file", Assert.Throws<InvalidDataException>(() => MidiFileReader.Read("RIFF0000WAVE"u8.ToArray())).Message);
        var smpte = Song();
        smpte[12] = 0xE7; // division with the SMPTE bit
        Assert.Contains("SMPTE", Assert.Throws<InvalidDataException>(() => MidiFileReader.Read(smpte)).Message);
        Assert.Throws<InvalidDataException>(() => MidiFileReader.Read(Song()[..40]));
        var format2 = Song();
        format2[9] = 2;
        Assert.Contains("format 2", Assert.Throws<InvalidDataException>(() => MidiFileReader.Read(format2)).Message);
    }

    // ---- Playing ----

    [Fact]
    public void Notes_play_at_their_time_and_follow_the_tempo_map()
    {
        var synth = new FakeSynth();
        var engine = Engine(MidiFileReader.Read(Song()), synth);
        engine.Play();
        Render(engine, 15);

        var guitar = synth.NoteOns(0).Select(n => n.Sample).ToList();
        double[] expected = [0, 2, 4, 8, 12, 15]; // bar starts in seconds
        Assert.Equal(expected.Length, guitar.Count);
        for (var i = 0; i < expected.Length; i++)
            Assert.InRange(guitar[i], expected[i] * Rate - MidiPlayerEngine.BlockSize, expected[i] * Rate + MidiPlayerEngine.BlockSize);
        Assert.Contains(synth.Sent, s => s is { Channel: 0, Command: 0xC0, Data1: 30 }); // program change sent
    }

    [Fact]
    public void Half_speed_doubles_the_time_without_touching_pitch()
    {
        var synth = new FakeSynth();
        var engine = Engine(MidiFileReader.Read(Song()), synth, new MidiPlayerSettings(Speed: 0.5));
        engine.Play();
        Render(engine, 9);

        var guitar = synth.NoteOns(0).ToList();
        Assert.InRange(guitar[2].Sample, 8.0 * Rate - 64, 8.0 * Rate + 64); // bar 3 at 4 s → 8 s
        Assert.All(guitar, n => Assert.Equal(64, n.Note));
    }

    [Fact]
    public void Muted_tracks_stay_silent_and_solo_mutes_the_rest()
    {
        var song = MidiFileReader.Read(Song());

        var muted = new FakeSynth();
        var engine = Engine(song, muted, new MidiPlayerSettings(Muted: 0b01)); // Lead muted
        engine.Play();
        Render(engine, 3);
        Assert.Empty(muted.NoteOns(0));
        Assert.Equal(2, muted.NoteOns(9).Count());

        var solo = new FakeSynth();
        engine = Engine(song, solo, new MidiPlayerSettings(Soloed: 0b01)); // Lead soloed → drums silent
        engine.Play();
        Render(engine, 3);
        Assert.Empty(solo.NoteOns(9));
        Assert.Equal(2, solo.NoteOns(0).Count());
    }

    [Fact]
    public void Muting_mid_note_releases_the_held_note()
    {
        var synth = new FakeSynth();
        var engine = Engine(MidiFileReader.Read(Song()), synth);
        engine.Play();
        Render(engine, 0.1);                       // Lead's first note is sounding
        engine.Settings = new MidiPlayerSettings(Muted: 0b01);
        Render(engine, 0.05);

        Assert.Contains(synth.Sent, s => s is { Channel: 0, Command: 0x80, Data1: 64 } && s.Sample < 0.2 * Rate);
    }

    [Fact]
    public void Seeking_restores_instruments_without_replaying_old_notes()
    {
        var synth = new FakeSynth();
        var engine = Engine(MidiFileReader.Read(Song()), synth);
        engine.Seek(5.0);
        engine.Play();
        Render(engine, 0.1);

        var afterReset = synth.Sent.Where(s => s.Sample >= synth.Resets[^1]).ToList();
        Assert.Contains(afterReset, s => s is { Command: 0xC0, Data1: 30 });   // program chased
        Assert.Contains(afterReset, s => s is { Command: 0xB0, Data1: 7 });    // volume chased
        Assert.DoesNotContain(afterReset, s => s.Command == 0x90);              // no old notes, and none due yet (next at 8 s)
        Assert.InRange(engine.Position, 5.0, 5.2);
    }

    [Fact]
    public void A_bar_loop_jumps_back_and_keeps_playing()
    {
        var song = MidiFileReader.Read(Song());
        var synth = new FakeSynth();
        var engine = Engine(song, synth, new MidiPlayerSettings(LoopEnabled: true, LoopFromBar: 2, LoopToBar: 2)); // bar 2 = 2–4 s
        engine.Seek(song.BarStartSeconds(2));
        engine.Play();
        Render(engine, 6.5);

        var guitar = synth.NoteOns(0).Select(n => n.Sample / (double)Rate).ToList();
        Assert.Equal(4, guitar.Count);                       // bar 2's note at 0, 2, 4, 6 s of playback
        Assert.All(guitar.Zip(new[] { 0.0, 2, 4, 6 }), p => Assert.InRange(p.First, p.Second - 0.01, p.Second + 0.01));
        Assert.True(engine.IsPlaying);
        Assert.InRange(engine.Position, 2.0, 4.0);
    }

    [Fact]
    public void Count_in_clicks_one_bar_then_the_song_starts()
    {
        var synth = new FakeSynth();
        var engine = Engine(MidiFileReader.Read(Song()), synth, new MidiPlayerSettings(CountIn: true, Muted: 0b10)); // drums muted
        engine.Play();
        Render(engine, 0.1);
        Assert.True(engine.IsCountingIn);
        Render(engine, 2.2);

        var clicks = synth.NoteOns(9).ToList();
        Assert.Equal([MidiPlayerEngine.CountInAccentNote, MidiPlayerEngine.CountInNote, MidiPlayerEngine.CountInNote, MidiPlayerEngine.CountInNote],
            clicks.Select(c => c.Note));
        Assert.All(clicks.Select((c, i) => (c.Sample, i)), c => Assert.InRange(c.Sample, c.i * 0.5 * Rate - 64, c.i * 0.5 * Rate + 64)); // 120 BPM
        var first = synth.NoteOns(0).First().Sample;
        Assert.InRange(first, 2.0 * Rate - 64, 2.0 * Rate + 128);                // after 4 beats of 0.5 s
        Assert.False(engine.IsCountingIn);
    }

    [Fact]
    public void Count_in_before_every_loop_pass_when_asked()
    {
        var song = MidiFileReader.Read(Song());
        List<double> GuitarTimes(bool eachLoop, out List<double> accents)
        {
            var synth = new FakeSynth();
            var engine = Engine(song, synth, new MidiPlayerSettings(LoopEnabled: true, LoopFromBar: 2, LoopToBar: 2, CountIn: true,
                CountInEachLoop: eachLoop)); // bar 2 = 2 s; count-in = 4 beats of 0.5 s
            engine.Seek(song.BarStartSeconds(2));
            engine.Play();
            Render(engine, 7.5);
            accents = [.. synth.NoteOns(9).Where(n => n.Note == MidiPlayerEngine.CountInAccentNote).Select(n => n.Sample / (double)Rate)];
            return [.. synth.NoteOns(0).Select(n => n.Sample / (double)Rate)];
        }

        var each = GuitarTimes(eachLoop: true, out var eachAccents);
        Assert.Equal(2, each.Count);                                       // count-in, bar, count-in, bar
        Assert.All(each.Zip(new[] { 2.0, 6.0 }), p => Assert.InRange(p.First, p.Second - 0.01, p.Second + 0.01));
        Assert.All(eachAccents.Zip(new[] { 0.0, 4.0 }), p => Assert.InRange(p.First, p.Second - 0.01, p.Second + 0.01));
        Assert.Equal(2, eachAccents.Count);

        var once = GuitarTimes(eachLoop: false, out var onceAccents);
        Assert.Equal(3, once.Count);                                       // count-in once, then the bar at 2, 4, 6 s
        Assert.Single(onceAccents);
    }

    [Fact]
    public void Count_in_reports_the_beat_it_is_on()
    {
        var engine = Engine(MidiFileReader.Read(Song()), new FakeSynth(), new MidiPlayerSettings(CountIn: true));
        engine.Play();
        Render(engine, 0.1);
        Assert.Equal(1, engine.CountInBeat);
        Render(engine, 0.6);                                               // 0.7 s: beat 2 (0.5–1 s)
        Assert.Equal(2, engine.CountInBeat);
        Render(engine, 1.5);
        Assert.Equal(0, engine.CountInBeat);                               // the song is playing
    }

    [Fact]
    public void A_track_volume_scales_its_channel_volume_including_the_files_own_changes()
    {
        var synth = new FakeSynth();
        var engine = Engine(MidiFileReader.Read(Song()), synth, new MidiPlayerSettings(TrackVolumes: [0.5f, 1f]));
        engine.Play();
        Render(engine, 0.1);
        IEnumerable<(long Sample, int Value)> Volumes(int channel) =>
            synth.Sent.Where(s => s.Channel == channel && s is { Command: 0xB0, Data1: 7 }).Select(s => (s.Sample, s.Data2));

        Assert.NotEmpty(Volumes(0));
        Assert.All(Volumes(0), v => Assert.Equal(50, v.Value));            // the file's CC 7 = 100, at 50 %
        Assert.Empty(Volumes(9));                                          // drums untouched

        engine.Settings = new MidiPlayerSettings(TrackVolumes: [0.25f, 1f]);
        Render(engine, 0.05);
        Assert.Equal(25, Volumes(0).Last().Value);                         // follows the slider while playing

        engine.Seek(5.0);                                                  // chase replays the file's CC 7, scaled
        Render(engine, 0.05);
        Assert.Equal(25, Volumes(0).Last().Value);
    }

    [Fact]
    public void The_count_in_is_at_full_level_even_when_the_drum_track_is_turned_down()
    {
        var synth = new FakeSynth();
        var engine = Engine(MidiFileReader.Read(Song()), synth, new MidiPlayerSettings(CountIn: true, TrackVolumes: [1f, 0.2f]));
        engine.Play();
        Render(engine, 2.5);

        var drums = synth.Sent.Where(s => s.Channel == 9 && s is { Command: 0xB0, Data1: 7 }).ToList();
        Assert.Single(drums);
        Assert.Equal(20, drums[0].Data2);                                  // turned down once the song starts
        Assert.InRange(drums[0].Sample, 2.0 * Rate - 64, 2.0 * Rate + 128);
    }

    [Fact]
    public void Bar_and_beat_follow_the_time_signature()
    {
        var song = MidiFileReader.Read(Song());
        Assert.Equal(new BarBeat(1, 2, 4), song.BarBeatAt(0.6));           // 120 BPM: beats of 0.5 s
        Assert.Equal(new BarBeat(2, 1, 4), song.BarBeatAt(2.0));
        Assert.Equal(new BarBeat(5, 2, 3), song.BarBeatAt(13.5));          // 3/4 at 60 BPM from 12 s
        Assert.Equal(new BarBeat(1, 1, 4), song.BarBeatAt(-1));
    }

    [Fact]
    public void Pause_releases_notes_and_play_resumes_where_it_was()
    {
        var synth = new FakeSynth();
        var engine = Engine(MidiFileReader.Read(Song()), synth);
        engine.Play();
        Render(engine, 1.0);
        engine.Pause();
        Render(engine, 1.0);
        var paused = engine.Position;
        Assert.InRange(paused, 0.99, 1.01);
        Assert.Contains(synth.Sent, s => s is { Command: 0xB0, Data1: 123 });   // all notes off
        Assert.False(engine.IsPlaying);

        engine.Play();
        Render(engine, 1.1);
        Assert.InRange(synth.NoteOns(0).ElementAt(1).Sample, 3.0 * Rate - 64, 3.0 * Rate + 64); // bar 2 (2 s of song) after 1 s paused
    }

    [Fact]
    public void Playback_stops_at_the_end()
    {
        var song = MidiFileReader.Read(Song());
        var synth = new FakeSynth();
        var engine = Engine(song, synth);
        engine.Seek(song.LengthSeconds - 0.5);
        engine.Play();
        Render(engine, 1);
        Assert.False(engine.IsPlaying);

        engine.Play();                     // at the end: Play starts over
        Render(engine, 0.05);
        Assert.True(engine.IsPlaying);
        Assert.InRange(engine.Position, 0, 0.1);
    }

    [Fact]
    public void Render_does_not_allocate()
    {
        // A synth that records nothing (FakeSynth's growing lists would be the only allocations).
        var engine = Engine(MidiFileReader.Read(Song()), new SilentSynth(), new MidiPlayerSettings(LoopEnabled: true, LoopFromBar: 5, LoopToBar: 6, CountIn: true,
            CountInEachLoop: true, TrackVolumes: [0.5f, 0.3f])); // incl. the last bar, loop count-ins and scaled volumes
        engine.Play();
        var buffer = new float[256];
        Allocations.AssertNone(() => engine.Render(buffer));
    }

    [Fact]
    public void Settings_are_normalised()
    {
        var s = new MidiPlayerSettings(Speed: 9, LoopFromBar: 0, LoopToBar: -3, Volume: 4).Normalized();
        Assert.Equal((MidiPlayerSettings.MaxSpeed, 1, 1, MidiPlayerSettings.MaxVolume), (s.Speed, s.LoopFromBar, s.LoopToBar, s.Volume));
        Assert.True(new MidiPlayerSettings(Muted: 0b10).IsSilenced(1));
        Assert.False(new MidiPlayerSettings(Muted: 0b10, Soloed: 0b10).IsSilenced(1));   // solo wins
    }

    [Fact]
    public void The_soft_limiter_is_linear_below_the_knee_and_never_exceeds_full_scale()
    {
        Assert.Equal(0.5f, MidiPlayerEngine.SoftLimit(0.5f));
        Assert.Equal(-0.7f, MidiPlayerEngine.SoftLimit(-0.7f));
        float[] loud = [0.8f, 1f, 2f, 10f, 1000f];
        Assert.All(loud, x => Assert.InRange(MidiPlayerEngine.SoftLimit(x), 0.7f, 1f));
        Assert.Equal(loud.Select(MidiPlayerEngine.SoftLimit).Order(), loud.Select(MidiPlayerEngine.SoftLimit)); // monotonic
        Assert.Equal(-MidiPlayerEngine.SoftLimit(3f), MidiPlayerEngine.SoftLimit(-3f));
    }

    // ---- The real SoundFont ----

    [Fact]
    public void The_bundled_soundfont_plays_a_note_in_tune()
    {
        var path = FindSoundFont();
        var synth = new SoundFontSynth(path, Rate);
        var song = MidiFileReader.Read(new Smf(Q, 0).Track((0, Smf.Program(0, 24)), (0, Smf.On(0, 57)), (4 * Q, Smf.Off(0, 57))).Build()); // A3 nylon guitar
        var engine = new MidiPlayerEngine(Rate, synth);
        engine.Load(song);
        engine.Play();

        var audio = new float[Rate];
        for (var i = 0; i < audio.Length; i += 256) engine.Render(audio.AsSpan(i, Math.Min(256, audio.Length - i)));

        Assert.True(audio.Max(Math.Abs) > 0.1f, "too quiet"); // one note at 100 %: clearly audible (was 0.02 before the gain change)
        var yin = new YinPitchDetector(Rate);
        var (note, cents) = Pitch.Nearest(yin.Detect(audio.AsSpan(Rate / 4, yin.RequiredSamples)).Hz);
        Assert.Equal(57, note);
        Assert.InRange(cents, -10, 10);
    }

    private static string FindSoundFont()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "GuitarPractice.App", "Assets", "SoundFont", "GeneralUser-GS.sf2");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("GeneralUser-GS.sf2 not found above the test directory");
    }
}
