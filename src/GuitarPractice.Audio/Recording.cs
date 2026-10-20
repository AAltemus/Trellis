using System.Diagnostics;
using System.Text;

namespace GuitarPractice.Audio;

/// <summary>A recorded (or loaded) mono take.</summary>
public sealed class AudioTake(float[] samples, int sampleRate)
{
    public float[] Samples { get; } = samples ?? throw new ArgumentNullException(nameof(samples));
    public int SampleRate { get; } = sampleRate > 0 ? sampleRate : throw new ArgumentOutOfRangeException(nameof(sampleRate));
    public TimeSpan Duration => TimeSpan.FromSeconds(Samples.Length / (double)SampleRate);
}

/// <summary>
/// Minimal RIFF/WAVE I/O. Writes 16-bit PCM mono (streamed, so long takes don't need a second copy in memory).
/// Reads PCM 16/24/32-bit and IEEE float 32-bit, any channel count (mixed down to mono).
/// </summary>
public static class WavFile
{
    public static void Write(string path, AudioTake take)
    {
        ArgumentNullException.ThrowIfNull(take);
        using var stream = File.Create(path);
        Write(stream, take);
    }

    public static void Write(Stream stream, AudioTake take)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(take);
        const short channels = 1, bits = 16;
        var dataBytes = take.Samples.Length * 2;
        using var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write(channels);
        w.Write(take.SampleRate);
        w.Write(take.SampleRate * channels * bits / 8);
        w.Write((short)(channels * bits / 8));
        w.Write(bits);
        w.Write("data"u8);
        w.Write(dataBytes);
        var buffer = new byte[64 * 1024];
        var n = 0;
        foreach (var s in take.Samples)
        {
            var v = (short)Math.Clamp(Math.Round(s * 32768.0), short.MinValue, short.MaxValue); // matches the /32768 read convention
            buffer[n++] = (byte)v;
            buffer[n++] = (byte)(v >> 8);
            if (n == buffer.Length)
            {
                w.Write(buffer, 0, n);
                n = 0;
            }
        }
        w.Write(buffer, 0, n);
    }

    /// <summary>Sample rate and duration from the header only (no sample data read).</summary>
    public static (int SampleRate, TimeSpan Duration) ReadInfo(string path)
    {
        using var stream = File.OpenRead(path);
        using var r = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        var format = ReadHeader(r, stream, out var dataBytes);
        return (format.Rate, TimeSpan.FromSeconds(dataBytes / (double)(format.Channels * format.Bits / 8) / format.Rate));
    }

    public static AudioTake Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static AudioTake Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var r = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        var format = ReadHeader(r, stream, out var dataBytes);
        var bytes = r.ReadBytes((int)dataBytes);
        return new AudioTake(Decode(bytes, format.Format, format.Channels, format.Bits), format.Rate);
    }

    private readonly record struct WavFormat(int Format, int Channels, int Rate, int Bits);

    /// <summary>
    /// Walks the RIFF chunks up to the start of the sample data and checks the format. Safe on broken or crafted files:
    /// chunk sizes are unsigned and clamped to what is left in the file (so a lying header can't make us allocate more
    /// than the file holds), every step moves forward (no endless loop), and only formats <see cref="Decode"/> handles
    /// get through. Leaves the stream at the first sample; <paramref name="dataBytes"/> is how many bytes follow.
    /// </summary>
    private static WavFormat ReadHeader(BinaryReader r, Stream stream, out long dataBytes)
    {
        if (stream.Length < 12 || Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF") throw new InvalidDataException("Not a RIFF file.");
        r.ReadUInt32();
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE") throw new InvalidDataException("Not a WAVE file.");

        WavFormat? format = null;
        while (stream.Length - stream.Position >= 8)
        {
            var id = Encoding.ASCII.GetString(r.ReadBytes(4));
            var size = Math.Min(r.ReadUInt32(), stream.Length - stream.Position); // a truncated file: use what's there
            var start = stream.Position;
            if (id == "fmt ")
            {
                if (size < 16) throw new InvalidDataException("WAV fmt chunk too short.");
                int tag = r.ReadUInt16(), channels = r.ReadUInt16();
                var rate = r.ReadInt32();
                r.ReadInt32(); // byte rate
                r.ReadUInt16(); // block align
                int bits = r.ReadUInt16();
                if (tag == 0xFFFE && size >= 26) tag = bits == 32 ? 3 : 1; // WAVE_FORMAT_EXTENSIBLE (approx.)
                if (channels is < 1 or > 64 || rate is < 1 or > 1_000_000 || bits is not (16 or 24 or 32)
                    || (tag != 1 && tag != 3) || (tag == 3 && bits != 32))
                    throw new InvalidDataException($"Unsupported WAV format {tag}, {bits}-bit, {channels} channels, {rate} Hz.");
                format = new WavFormat(tag, channels, rate, bits);
            }
            else if (id == "data")
            {
                if (format is not { } f) throw new InvalidDataException("WAV data before a valid fmt chunk.");
                if (size > int.MaxValue) throw new InvalidDataException("WAV file too large.");
                dataBytes = size;
                return f;
            }
            stream.Position = Math.Min(stream.Length, start + size + (size & 1)); // chunks are padded to even sizes
        }
        throw new InvalidDataException("WAV file has no data chunk.");
    }

    private static float[] Decode(byte[] bytes, int format, int channels, int bits)
    {
        var bytesPerSample = bits / 8;
        if (bytesPerSample is < 2 or > 4 || (format != 1 && format != 3) || (format == 3 && bits != 32))
            throw new InvalidDataException($"Unsupported WAV format {format}, {bits}-bit.");
        var frames = bytes.Length / (bytesPerSample * channels);
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var o = (f * channels + c) * bytesPerSample;
                sum += format == 3 ? BitConverter.ToSingle(bytes, o)
                    : bits == 16 ? BitConverter.ToInt16(bytes, o) / 32768.0
                    : bits == 24 ? ((bytes[o] | bytes[o + 1] << 8 | (sbyte)bytes[o + 2] << 16)) / 8388608.0
                    : BitConverter.ToInt32(bytes, o) / 2147483648.0;
            }
            mono[f] = (float)(sum / channels);
        }
        return mono;
    }
}

/// <summary>Min/max per display column, for drawing a waveform.</summary>
public sealed record WaveformPeaks(float[] Min, float[] Max)
{
    public static WaveformPeaks Compute(ReadOnlySpan<float> samples, int columns)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        var min = new float[columns];
        var max = new float[columns];
        if (samples.Length == 0) return new WaveformPeaks(min, max);
        for (var c = 0; c < columns; c++)
        {
            var from = (int)((long)c * samples.Length / columns);
            var to = Math.Max(from + 1, (int)((long)(c + 1) * samples.Length / columns));
            float lo = float.MaxValue, hi = float.MinValue;
            for (var i = from; i < to && i < samples.Length; i++)
            {
                lo = Math.Min(lo, samples[i]);
                hi = Math.Max(hi, samples[i]);
            }
            min[c] = lo;
            max[c] = hi;
        }
        return new WaveformPeaks(min, max);
    }
}

/// <summary>
/// Records the input into memory. The capture callback only copies into a lock-free ring (~6 s); a background thread
/// moves it into 64k-sample chunks (allocation happens there, never on the audio thread). Stops accepting audio at
/// <see cref="MaxLength"/>.
/// </summary>
public sealed class TakeRecorder : IDisposable
{
    public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(10);
    private const int RingSize = 1 << 18;
    private const int ChunkSize = 1 << 16;

    private readonly IAudioInput _input;
    private readonly bool _backgroundDrain;
    private readonly float[] _ring = new float[RingSize];
    private readonly AudioCaptureCallback _captureCallback;
    private readonly List<float[]> _chunks = [];
    private long _written;   // capture thread
    private long _read;      // drain thread: ring read position
    private long _stored;    // samples kept in _chunks (≤ _maxSamples)
    private long _dropped;
    private int _chunkFill;
    private long _maxSamples;
    private Thread? _thread;
    private volatile bool _stopping;
    private volatile bool _recording;
    private double _peak;     // drain thread: decaying peak, linear

    /// <param name="backgroundDrain">False for tests: call <see cref="Drain"/> yourself.</param>
    public TakeRecorder(IAudioInput input, bool backgroundDrain = true)
    {
        ArgumentNullException.ThrowIfNull(input);
        _input = input;
        _backgroundDrain = backgroundDrain;
        _captureCallback = OnCapture;
    }

    public bool IsRecording => _recording;
    public int SampleRate { get; private set; }
    public long SamplesRecorded => Volatile.Read(ref _stored);
    public TimeSpan Elapsed => SampleRate > 0 ? TimeSpan.FromSeconds(SamplesRecorded / (double)SampleRate) : TimeSpan.Zero;
    public bool LimitReached => SamplesRecorded >= _maxSamples && _maxSamples > 0;

    /// <summary>Samples lost because the drain thread fell more than ~6 s behind (should stay 0).</summary>
    public long DroppedSamples => Interlocked.Read(ref _dropped);

    /// <summary>Recent input peak in dBFS (decays ~20 dB/s), for a level meter.</summary>
    public double PeakDb
    {
        get
        {
            var p = Volatile.Read(ref _peak);
            return p > 0 ? 20 * Math.Log10(p) : double.NegativeInfinity;
        }
    }

    public void Start(string? deviceId = null, int channel = 0)
    {
        if (_recording) throw new InvalidOperationException("Already recording.");
        SampleRate = _input.GetPreferredSampleRate(deviceId);
        _chunks.Clear();
        _chunkFill = 0;
        Volatile.Write(ref _written, 0);
        Volatile.Write(ref _read, 0);
        Volatile.Write(ref _stored, 0);
        Interlocked.Exchange(ref _dropped, 0);
        Volatile.Write(ref _peak, 0);
        _maxSamples = (long)(MaxLength.TotalSeconds * SampleRate);
        _recording = true;
        _input.Start(new AudioStreamOptions(deviceId, SampleRate, 512, channel), _captureCallback);

        if (_backgroundDrain)
        {
            _stopping = false;
            _thread = new Thread(DrainLoop) { IsBackground = true, Name = "Recorder drain" };
            _thread.Start();
        }
    }

    /// <summary>Stops and returns everything recorded.</summary>
    public AudioTake Stop()
    {
        _input.Stop();
        _recording = false;
        _stopping = true;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        Drain();

        var total = (int)SamplesRecorded;
        var samples = new float[total];
        var offset = 0;
        foreach (var chunk in _chunks)
        {
            var n = Math.Min(chunk.Length, total - offset);
            Array.Copy(chunk, 0, samples, offset, n);
            offset += n;
        }
        _chunks.Clear();
        return new AudioTake(samples, SampleRate);
    }

    /// <summary>Moves captured audio from the ring into chunks. Called by the drain thread (or tests).</summary>
    public void Drain()
    {
        var written = Volatile.Read(ref _written);
        var read = _read;
        if (written - read > RingSize) // overrun: skip what was overwritten
        {
            Interlocked.Add(ref _dropped, written - read - RingSize);
            read = written - RingSize;
        }

        var peak = Volatile.Read(ref _peak) * 0.9;
        var stored = _stored;
        while (read < written && stored < _maxSamples)
        {
            if (_chunks.Count == 0 || _chunkFill == ChunkSize)
            {
                _chunks.Add(new float[ChunkSize]);
                _chunkFill = 0;
            }
            var chunk = _chunks[^1];
            var n = (int)Math.Min(Math.Min(ChunkSize - _chunkFill, written - read), _maxSamples - stored);
            for (var i = 0; i < n; i++)
            {
                var v = _ring[(read + i) & (RingSize - 1)];
                chunk[_chunkFill + i] = v;
                peak = Math.Max(peak, Math.Abs(v));
            }
            _chunkFill += n;
            read += n;
            stored += n;
        }
        if (read < written) read = written; // over the limit: discard the rest
        Volatile.Write(ref _peak, peak);
        Volatile.Write(ref _read, read);
        Volatile.Write(ref _stored, stored);
    }

    public void Dispose()
    {
        if (_recording) Stop();
    }

    private void OnCapture(ReadOnlySpan<float> samples)
    {
        if (!_recording) return;
        var written = _written;
        foreach (var x in samples) _ring[written++ & (RingSize - 1)] = x;
        Volatile.Write(ref _written, written);
    }

    private void DrainLoop()
    {
        while (!_stopping)
        {
            try
            {
                Drain();
            }
#pragma warning disable CA1031 // Keep recording; a failed drain is logged.
            catch (Exception ex)
            {
                Trace.TraceError($"Recorder drain failed: {ex.Message}");
            }
#pragma warning restore CA1031
            Thread.Sleep(20);
        }
    }
}
