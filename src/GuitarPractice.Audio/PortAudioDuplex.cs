using System.Diagnostics;
using PortAudioSharp;
using PaStream = PortAudioSharp.Stream;

namespace GuitarPractice.Audio;

/// <summary>
/// <see cref="IAudioDuplex"/> on PortAudio: one full-duplex stream, so every input sample and output sample share a
/// clock and the round trip (output → speaker/cable → input) is the same each time the stream runs with the same
/// devices and settings (the looper calibrates it once). The output is the default output if it's on the input's
/// driver, else the first output on that driver. Mono in (chosen channel or both mixed) and mono out (to every output
/// channel); no allocation or locks in the callback.
/// </summary>
public sealed class PortAudioDuplex : IAudioDuplex
{
    private readonly PaStream.Callback _paCallback; // kept in a field so the GC never collects the native thunk
    private PaStream? _stream;
    private AudioDuplexCallback? _callback;
    private float[] _in = [];
    private float[] _out = [];
    private int _inChannels;
    private int _outChannels;
    private int _channel;
    private bool _initialized;

    public PortAudioDuplex()
    {
        _paCallback = OnAudio;
    }

    public int SampleRate { get; private set; }

    public bool IsRunning => _stream is { IsActive: true };

    /// <summary>"Input name → output name", for display.</summary>
    public string? DeviceNames { get; private set; }

    public void Start(AudioStreamOptions options, AudioDuplexCallback callback)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(callback);
        Stop();
        EnsureInitialized();

        var inDevice = PortAudioHost.ResolveDevice(options.DeviceId, input: true);
        var inInfo = PortAudio.GetDeviceInfo(inDevice);
        var outDevice = OutputOnSameDriver(inInfo.hostApi);
        var outInfo = PortAudio.GetDeviceInfo(outDevice);
        _inChannels = Math.Min(2, Math.Max(1, inInfo.maxInputChannels));
        _outChannels = Math.Min(2, Math.Max(1, outInfo.maxOutputChannels));
        _channel = options.Channel >= 1 && options.Channel <= _inChannels ? options.Channel : 0;
        var scratch = Math.Max(options.FramesPerBuffer, 4_096);
        _in = new float[scratch];
        _out = new float[scratch];
        _callback = callback;

        _stream = new PaStream(
            inParams: new StreamParameters
            {
                device = inDevice, channelCount = _inChannels, sampleFormat = SampleFormat.Float32,
                suggestedLatency = inInfo.defaultLowInputLatency, hostApiSpecificStreamInfo = IntPtr.Zero,
            },
            outParams: new StreamParameters
            {
                device = outDevice, channelCount = _outChannels, sampleFormat = SampleFormat.Float32,
                suggestedLatency = outInfo.defaultLowOutputLatency, hostApiSpecificStreamInfo = IntPtr.Zero,
            },
            sampleRate: options.SampleRate,
            framesPerBuffer: (uint)options.FramesPerBuffer,
            streamFlags: StreamFlags.ClipOff,
            callback: _paCallback,
            userData: null);
        SampleRate = options.SampleRate;
        DeviceNames = $"{inInfo.name.Trim()} → {outInfo.name.Trim()}";
        _stream.Start();
    }

    /// <summary>The input device's preferred rate (open the stream at it).</summary>
    public int GetPreferredSampleRate(string? inputDeviceId)
    {
        EnsureInitialized();
        return PortAudioHost.PreferredSampleRate(inputDeviceId, input: true);
    }

    private static int OutputOnSameDriver(int hostApi)
    {
        var fallback = PortAudio.DefaultOutputDevice;
        if (fallback != PortAudio.NoDevice && PortAudio.GetDeviceInfo(fallback).hostApi == hostApi) return fallback;
        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var d = PortAudio.GetDeviceInfo(i);
            if (d.hostApi == hostApi && d.maxOutputChannels > 0) return i;
        }
        throw new InvalidOperationException("No output on the input's audio driver.");
    }

    public void Stop()
    {
        var stream = _stream;
        _stream = null;
        if (stream is null) return;
        try
        {
            if (!stream.IsStopped) stream.Stop();
        }
        finally
        {
            stream.Dispose();
        }
    }

    public void Dispose()
    {
        Stop();
        if (!_initialized) return;
        _initialized = false;
        PortAudioHost.Release();
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        PortAudioHost.Acquire();
        _initialized = true;
    }

    private unsafe StreamCallbackResult OnAudio(
        IntPtr input, IntPtr output, uint frameCount, ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags, IntPtr userDataPtr)
    {
        var destination = new Span<float>((void*)output, (int)frameCount * _outChannels);
        var callback = _callback;
        if (callback is null)
        {
            destination.Clear();
            return StreamCallbackResult.Continue;
        }

        try
        {
            var source = input == IntPtr.Zero
                ? ReadOnlySpan<float>.Empty
                : new ReadOnlySpan<float>((void*)input, (int)frameCount * _inChannels);
            var done = 0;
            while (done < frameCount)
            {
                var count = Math.Min(_in.Length, (int)frameCount - done);
                var mono = _in.AsSpan(0, count);
                if (source.IsEmpty) mono.Clear();
                else
                {
                    var frames = source.Slice(done * _inChannels, count * _inChannels);
                    if (_inChannels == 1) frames.CopyTo(mono);
                    else if (_channel == 0)
                        for (var i = 0; i < count; i++) mono[i] = (frames[2 * i] + frames[2 * i + 1]) * 0.5f;
                    else
                        for (var i = 0; i < count; i++) mono[i] = frames[2 * i + _channel - 1];
                }

                var outMono = _out.AsSpan(0, count);
                callback(mono, outMono);
                var outFrames = destination.Slice(done * _outChannels, count * _outChannels);
                if (_outChannels == 1) outMono.CopyTo(outFrames);
                else
                    for (var i = 0; i < count; i++)
                        for (var c = 0; c < _outChannels; c++)
                            outFrames[i * _outChannels + c] = outMono[i];
                done += count;
            }
        }
#pragma warning disable CA1031 // An exception must never escape into native code; play silence instead.
        catch (Exception ex)
        {
            destination.Clear();
            Trace.TraceError($"Duplex audio failed: {ex.Message}");
        }
#pragma warning restore CA1031
        return StreamCallbackResult.Continue;
    }
}
