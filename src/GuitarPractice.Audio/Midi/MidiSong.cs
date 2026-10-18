using System.Text;

namespace GuitarPractice.Audio.Midi;

/// <summary>A channel message at a tick: note on/off, controller, program change, pitch bend …</summary>
public readonly record struct MidiEvent(long Tick, int Track, byte Status, byte Data1, byte Data2)
{
    public int Channel => Status & 0x0F;
    public int Command => Status & 0xF0;
    public bool IsNoteOn => Command == 0x90 && Data2 > 0;
    public bool IsNoteOff => Command == 0x80 || (Command == 0x90 && Data2 == 0);
}

/// <summary>A track that plays something: its name (from the file), the channels it uses and its first instrument.</summary>
public sealed record MidiTrackInfo(int Index, string Name, IReadOnlyList<int> Channels, string Instrument, int NoteCount)
{
    public bool IsDrums => Channels.Count > 0 && Channels.All(c => c == MidiSong.DrumChannel);
}

public readonly record struct TempoChange(long Tick, int MicrosecondsPerQuarter)
{
    public double QuarterBpm => 60_000_000.0 / MicrosecondsPerQuarter;
}

public readonly record struct TimeSignature(long Tick, int Numerator, int Denominator);

/// <summary>A position as bar and beat (1-based) and how many beats that bar has.</summary>
public readonly record struct BarBeat(int Bar, int Beat, int BeatsInBar);

/// <summary>
/// A parsed standard MIDI file (format 0 or 1), ready to play: channel events in time order with their time in seconds
/// (from the tempo map, at 100 % speed), the tracks that play something, time signatures and bar starts.
/// </summary>
public sealed class MidiSong
{
    public const int DrumChannel = 9;
    public const int DefaultMicrosecondsPerQuarter = 500_000; // 120 BPM

    private readonly double[] _tempoSeconds; // song time at each tempo change
    private readonly long[] _barTicks;

    internal MidiSong(int ticksPerQuarter, IReadOnlyList<MidiEvent> events, IReadOnlyList<MidiTrackInfo> tracks,
        IReadOnlyList<TempoChange> tempos, IReadOnlyList<TimeSignature> timeSignatures)
    {
        TicksPerQuarter = ticksPerQuarter;
        Events = events;
        Tracks = tracks;
        Tempos = tempos.Count > 0 && tempos[0].Tick == 0 ? tempos : [new TempoChange(0, DefaultMicrosecondsPerQuarter), .. tempos];
        TimeSignatures = timeSignatures.Count > 0 && timeSignatures[0].Tick == 0 ? timeSignatures : [new TimeSignature(0, 4, 4), .. timeSignatures];

        _tempoSeconds = new double[Tempos.Count];
        for (var i = 1; i < Tempos.Count; i++)
            _tempoSeconds[i] = _tempoSeconds[i - 1] + (Tempos[i].Tick - Tempos[i - 1].Tick) * Tempos[i - 1].MicrosecondsPerQuarter / 1e6 / TicksPerQuarter;

        var seconds = new double[events.Count];
        for (var i = 0; i < events.Count; i++) seconds[i] = SecondsAt(events[i].Tick);
        EventSeconds = seconds;

        LengthTicks = events.Count > 0 ? events[^1].Tick : 0;
        _barTicks = BuildBars();
        LengthSeconds = SecondsAt(BarEndTick(BarCount)); // play to the end of the last bar
    }

    public int TicksPerQuarter { get; }

    /// <summary>Channel events (no meta/sysex), by tick; ties keep file order.</summary>
    public IReadOnlyList<MidiEvent> Events { get; }

    /// <summary><see cref="Events"/>[i] happens <see cref="EventSeconds"/>[i] seconds into the song (100 % speed).</summary>
    public IReadOnlyList<double> EventSeconds { get; }

    public IReadOnlyList<MidiTrackInfo> Tracks { get; }
    public IReadOnlyList<TempoChange> Tempos { get; }
    public IReadOnlyList<TimeSignature> TimeSignatures { get; }

    /// <summary>Tick of the last event.</summary>
    public long LengthTicks { get; }

    /// <summary>To the end of the last bar.</summary>
    public double LengthSeconds { get; }

    public int BarCount => _barTicks.Length;

    public double SecondsAt(long tick)
    {
        var i = TempoIndexAtTick(tick);
        return _tempoSeconds[i] + (tick - Tempos[i].Tick) * Tempos[i].MicrosecondsPerQuarter / 1e6 / TicksPerQuarter;
    }

    public long TickAt(double seconds)
    {
        var i = 0;
        while (i + 1 < Tempos.Count && _tempoSeconds[i + 1] <= seconds) i++;
        return Tempos[i].Tick + (long)Math.Round((seconds - _tempoSeconds[i]) * 1e6 * TicksPerQuarter / Tempos[i].MicrosecondsPerQuarter);
    }

    /// <summary>The quarter-note tempo in effect at <paramref name="seconds"/>.</summary>
    public double QuarterBpmAt(double seconds) => Tempos[TempoIndexAtTick(TickAt(Math.Max(0, seconds)))].QuarterBpm;

    /// <summary>The time signature in effect at <paramref name="tick"/>. Allocation-free (used on the audio thread).</summary>
    public TimeSignature TimeSignatureAt(long tick)
    {
        var i = 0;
        while (i + 1 < TimeSignatures.Count && TimeSignatures[i + 1].Tick <= tick) i++;
        return TimeSignatures[i];
    }

    /// <summary>Start of bar <paramref name="bar"/> (1-based; clamped to the song).</summary>
    public long BarStartTick(int bar) => _barTicks.Length == 0 ? 0 : _barTicks[Math.Clamp(bar, 1, _barTicks.Length) - 1];

    /// <summary>End of bar <paramref name="bar"/> = start of the next one (the last bar ends one bar-length after it starts).</summary>
    public long BarEndTick(int bar)
    {
        if (_barTicks.Length == 0) return 0;
        bar = Math.Clamp(bar, 1, _barTicks.Length);
        return bar < _barTicks.Length ? _barTicks[bar] : _barTicks[bar - 1] + BarLength(TimeSignatureAt(_barTicks[bar - 1]));
    }

    public double BarStartSeconds(int bar) => SecondsAt(BarStartTick(bar));
    public double BarEndSeconds(int bar) => SecondsAt(BarEndTick(bar));

    /// <summary>The bar (1-based) playing at <paramref name="seconds"/>.</summary>
    public int BarAt(double seconds)
    {
        if (_barTicks.Length == 0) return 1;
        var tick = TickAt(Math.Max(0, seconds));
        var index = Array.BinarySearch(_barTicks, tick);
        return Math.Clamp(index >= 0 ? index + 1 : ~index, 1, _barTicks.Length);
    }

    /// <summary>
    /// Bar and beat (both 1-based) at <paramref name="seconds"/>; a beat is the time signature's denominator (6/8 has six
    /// eighth-note beats).
    /// </summary>
    public BarBeat BarBeatAt(double seconds)
    {
        var bar = BarAt(seconds);
        var start = BarStartTick(bar);
        var sig = TimeSignatureAt(start);
        var ticksPerBeat = TicksPerQuarter * 4.0 / sig.Denominator;
        var beat = (int)((TickAt(Math.Max(0, seconds)) - start) / ticksPerBeat) + 1;
        return new BarBeat(bar, Math.Clamp(beat, 1, Math.Max(1, sig.Numerator)), Math.Max(1, sig.Numerator));
    }

    private int TempoIndexAtTick(long tick)
    {
        var i = 0;
        while (i + 1 < Tempos.Count && Tempos[i + 1].Tick <= tick) i++;
        return i;
    }

    private long BarLength(TimeSignature ts) => (long)TicksPerQuarter * 4 * ts.Numerator / ts.Denominator;

    /// <summary>
    /// Bar starts from tick 0 up to (not including) the last event's tick, following time-signature changes (a change
    /// mid-bar starts a new bar there). A final note-off exactly on a bar line doesn't open another bar.
    /// </summary>
    private long[] BuildBars()
    {
        var bars = new List<long>();
        var tick = 0L;
        var sig = 0;
        do
        {
            bars.Add(tick);
            var next = tick + Math.Max(1, BarLength(TimeSignatures[sig]));
            if (sig + 1 < TimeSignatures.Count && TimeSignatures[sig + 1].Tick <= next)
            {
                sig++;
                next = Math.Max(TimeSignatures[sig].Tick, tick + 1);
            }
            tick = next;
        }
        while (tick < LengthTicks && bars.Count < 100_000);
        return [.. bars];
    }
}

/// <summary>Reads standard MIDI files (SMF format 0 and 1, ticks-per-quarter timing) such as Guitar Pro exports.</summary>
public static class MidiFileReader
{
    public static MidiSong Read(string path) => Read(File.ReadAllBytes(path));

    public static MidiSong Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var r = new Reader(data);
        if (r.Remaining < 14 || r.Ascii(4) != "MThd") throw new InvalidDataException("Not a MIDI file (no MThd header).");
        var headerLength = r.UInt32();
        var format = r.UInt16();
        var trackCount = r.UInt16();
        var division = r.UInt16();
        r.Skip((int)headerLength - 6);
        if (format > 1) throw new InvalidDataException($"MIDI format {format} isn't supported (only 0 and 1).");
        if ((division & 0x8000) != 0) throw new InvalidDataException("SMPTE-timed MIDI files aren't supported.");
        if (division == 0) throw new InvalidDataException("Invalid MIDI timing (0 ticks per quarter).");

        var events = new List<MidiEvent>();
        var tempos = new List<TempoChange>();
        var signatures = new List<TimeSignature>();
        var names = new List<string>();
        var track = 0;
        while (r.Remaining >= 8 && track < trackCount)
        {
            var id = r.Ascii(4);
            var length = (int)Math.Min(r.UInt32(), (uint)r.Remaining);
            if (id != "MTrk")
            {
                r.Skip(length);
                continue;
            }
            names.Add(ReadTrack(r.Slice(length), track, events, tempos, signatures));
            track++;
        }

        // Stable by tick: List.Sort isn't stable, so sort by (tick, original index).
        var ordered = events.Select((e, i) => (e, i)).OrderBy(x => x.e.Tick).ThenBy(x => x.i).Select(x => x.e).ToList();
        tempos.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        signatures.Sort((a, b) => a.Tick.CompareTo(b.Tick));

        var tracks = new List<MidiTrackInfo>();
        for (var t = 0; t < names.Count; t++)
        {
            var mine = ordered.Where(e => e.Track == t).ToList();
            var notes = mine.Count(e => e.IsNoteOn);
            if (notes == 0) continue;
            var channels = mine.Where(e => e.IsNoteOn).Select(e => e.Channel).Distinct().Order().ToList();
            var program = mine.FirstOrDefault(e => e.Command == 0xC0 && channels.Contains(e.Channel));
            var instrument = channels.All(c => c == MidiSong.DrumChannel) ? "Drums"
                : program.Status != 0 ? GeneralMidi.Instrument(program.Data1) : GeneralMidi.Instrument(0);
            var name = string.IsNullOrWhiteSpace(names[t]) ? instrument : names[t].Trim();
            tracks.Add(new MidiTrackInfo(t, name, channels, instrument, notes));
        }
        return new MidiSong(division, ordered, tracks, tempos, signatures);
    }

    private static string ReadTrack(Reader r, int track, List<MidiEvent> events, List<TempoChange> tempos, List<TimeSignature> signatures)
    {
        var tick = 0L;
        byte running = 0;
        var name = "";
        while (r.Remaining > 0)
        {
            tick += r.VarLen();
            if (r.Remaining == 0) break;
            var status = r.Byte();
            if (status == 0xFF)
            {
                running = 0;
                var type = r.Byte();
                var length = (int)r.VarLen();
                var data = r.Bytes(length);
                switch (type)
                {
                    case 0x03 when name.Length == 0:
                        name = Encoding.Latin1.GetString(data);
                        break;
                    case 0x51 when data.Length >= 3:
                        var micro = (data[0] << 16) | (data[1] << 8) | data[2];
                        if (micro > 0) tempos.Add(new TempoChange(tick, micro));
                        break;
                    case 0x58 when data.Length >= 2 && data[0] > 0 && data[1] < 8:
                        signatures.Add(new TimeSignature(tick, data[0], 1 << data[1]));
                        break;
                    case 0x2F:
                        return name;
                }
                continue;
            }
            if (status is 0xF0 or 0xF7)
            {
                running = 0;
                r.Skip((int)r.VarLen());
                continue;
            }

            byte data1;
            if (status < 0x80)
            {
                if (running == 0) throw new InvalidDataException("Corrupt MIDI track (data without a status byte).");
                data1 = status;
                status = running;
            }
            else
            {
                running = status;
                data1 = r.Byte();
            }
            var command = status & 0xF0;
            var data2 = command is 0xC0 or 0xD0 ? (byte)0 : r.Byte();
            events.Add(new MidiEvent(tick, track, status, (byte)(data1 & 0x7F), (byte)(data2 & 0x7F)));
        }
        return name;
    }

    private sealed class Reader(byte[] data, int start = 0, int end = -1)
    {
        private readonly int _end = end < 0 ? data.Length : end;
        private int _pos = start;

        public int Remaining => _end - _pos;

        public byte Byte()
        {
            if (_pos >= _end) throw new InvalidDataException("Truncated MIDI file.");
            return data[_pos++];
        }

        public byte[] Bytes(int count)
        {
            if (count < 0 || count > Remaining) throw new InvalidDataException("Truncated MIDI file.");
            var result = data.AsSpan(_pos, count).ToArray();
            _pos += count;
            return result;
        }

        public void Skip(int count) => _pos = Math.Min(_end, _pos + Math.Max(0, count));
        public string Ascii(int count) => Encoding.ASCII.GetString(Bytes(count));
        public ushort UInt16() => (ushort)((Byte() << 8) | Byte());
        public uint UInt32() => ((uint)Byte() << 24) | ((uint)Byte() << 16) | ((uint)Byte() << 8) | Byte();

        public long VarLen()
        {
            long value = 0;
            for (var i = 0; i < 4; i++)
            {
                var b = Byte();
                value = (value << 7) | (uint)(b & 0x7F);
                if ((b & 0x80) == 0) return value;
            }
            throw new InvalidDataException("Corrupt MIDI file (variable-length number too long).");
        }

        public Reader Slice(int length)
        {
            var slice = new Reader(data, _pos, _pos + length);
            _pos += length;
            return slice;
        }
    }
}

/// <summary>General MIDI program names (0-based program number).</summary>
public static class GeneralMidi
{
    private static readonly string[] Names =
    [
        "Acoustic Grand Piano", "Bright Acoustic Piano", "Electric Grand Piano", "Honky-tonk Piano", "Electric Piano 1",
        "Electric Piano 2", "Harpsichord", "Clavinet", "Celesta", "Glockenspiel", "Music Box", "Vibraphone", "Marimba",
        "Xylophone", "Tubular Bells", "Dulcimer", "Drawbar Organ", "Percussive Organ", "Rock Organ", "Church Organ",
        "Reed Organ", "Accordion", "Harmonica", "Tango Accordion", "Nylon Guitar", "Steel Guitar", "Jazz Guitar",
        "Clean Guitar", "Muted Guitar", "Overdriven Guitar", "Distortion Guitar", "Guitar Harmonics", "Acoustic Bass",
        "Fingered Bass", "Picked Bass", "Fretless Bass", "Slap Bass 1", "Slap Bass 2", "Synth Bass 1", "Synth Bass 2",
        "Violin", "Viola", "Cello", "Contrabass", "Tremolo Strings", "Pizzicato Strings", "Orchestral Harp", "Timpani",
        "String Ensemble 1", "String Ensemble 2", "Synth Strings 1", "Synth Strings 2", "Choir Aahs", "Voice Oohs",
        "Synth Voice", "Orchestra Hit", "Trumpet", "Trombone", "Tuba", "Muted Trumpet", "French Horn", "Brass Section",
        "Synth Brass 1", "Synth Brass 2", "Soprano Sax", "Alto Sax", "Tenor Sax", "Baritone Sax", "Oboe", "English Horn",
        "Bassoon", "Clarinet", "Piccolo", "Flute", "Recorder", "Pan Flute", "Blown Bottle", "Shakuhachi", "Whistle",
        "Ocarina", "Square Lead", "Sawtooth Lead", "Calliope Lead", "Chiff Lead", "Charang Lead", "Voice Lead",
        "Fifths Lead", "Bass + Lead", "New Age Pad", "Warm Pad", "Polysynth Pad", "Choir Pad", "Bowed Pad", "Metallic Pad",
        "Halo Pad", "Sweep Pad", "Rain FX", "Soundtrack FX", "Crystal FX", "Atmosphere FX", "Brightness FX", "Goblins FX",
        "Echoes FX", "Sci-fi FX", "Sitar", "Banjo", "Shamisen", "Koto", "Kalimba", "Bagpipe", "Fiddle", "Shanai",
        "Tinkle Bell", "Agogo", "Steel Drums", "Woodblock", "Taiko Drum", "Melodic Tom", "Synth Drum", "Reverse Cymbal",
        "Guitar Fret Noise", "Breath Noise", "Seashore", "Bird Tweet", "Telephone Ring", "Helicopter", "Applause", "Gunshot",
    ];

    public static string Instrument(int program) => Names[Math.Clamp(program, 0, Names.Length - 1)];
}
