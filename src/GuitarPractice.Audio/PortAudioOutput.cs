using System.Diagnostics;
using PortAudioSharp;
using PaStream = PortAudioSharp.Stream;

namespace GuitarPractice.Audio;

/// <summary>
/// <see cref="IAudioOutput"/> on PortAudio (PortAudioSharp2 + its bundled native library for win/osx/linux).
/// The render callback produces mono; this class duplicates it to every output channel. The callback runs on
/// PortAudio's thread: no allocation or locks there (the mono scratch buffer is allocated before the stream opens).
/// Device ids are PortAudio device indexes as strings.
/// </summary>
public sealed class PortAudioOutput : IAudioOutput
{
    private readonly PaStream.Callback _paCallback; // kept in a field so the GC never collects the native thunk
    private PaStream? _stream;
    private AudioRenderCallback? _render;
    private float[] _mono = [];
    private int _channels;
    private double _suggestedLatency;
    private long _measuredLatencyTicks; // TimeSpan ticks, written by the audio thread
    private bool _initialized;

    public PortAudioOutput()
    {
        _paCallback = OnAudio;
    }

    public IReadOnlyList<AudioDeviceInfo> Devices
    {
        get
        {
            EnsureInitialized();
            return PortAudioHost.ListDevices(input: false);
        }
    }

    public int SampleRate { get; private set; }

    /// <summary>Measured per buffer (DAC time − callback time) when the host reports it, else PortAudio's suggestion.</summary>
    public TimeSpan OutputLatency
    {
        get
        {
            var measured = Interlocked.Read(ref _measuredLatencyTicks);
            return measured > 0 ? TimeSpan.FromTicks(measured) : TimeSpan.FromSeconds(_suggestedLatency);
        }
    }

    public bool IsRunning => _stream is { IsActive: true };

    /// <summary>Name of the device the stream is open on, for display.</summary>
    public string? DeviceName { get; private set; }

    public int GetPreferredSampleRate(string? deviceId = null)
    {
        EnsureInitialized();
        return PortAudioHost.PreferredSampleRate(deviceId, input: false);
    }

    public void Start(AudioStreamOptions options, AudioRenderCallback callback)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(callback);
        Stop();
        EnsureInitialized();

        var device = PortAudioHost.ResolveDevice(options.DeviceId, input: false);
        var info = PortAudio.GetDeviceInfo(device);
        _channels = Math.Min(2, Math.Max(1, info.maxOutputChannels));
        _mono = new float[Math.Max(options.FramesPerBuffer, 4_096)];
        _render = callback;
        _suggestedLatency = info.defaultLowOutputLatency;
        Interlocked.Exchange(ref _measuredLatencyTicks, 0);

        var parameters = new StreamParameters
        {
            device = device,
            channelCount = _channels,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = info.defaultLowOutputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        _stream = new PaStream(
            inParams: null,
            outParams: parameters,
            sampleRate: options.SampleRate,
            framesPerBuffer: (uint)options.FramesPerBuffer,
            streamFlags: StreamFlags.ClipOff,
            callback: _paCallback,
            userData: null);
        SampleRate = options.SampleRate;
        DeviceName = info.name;
        _stream.Start();
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
        var latency = timeInfo.outputBufferDacTime - timeInfo.currentTime;
        if (latency is > 0 and < 1) Interlocked.Exchange(ref _measuredLatencyTicks, (long)(latency * TimeSpan.TicksPerSecond));

        var destination = new Span<float>((void*)output, (int)frameCount * _channels);
        var render = _render;
        if (render is null)
        {
            destination.Clear();
            return StreamCallbackResult.Continue;
        }

        try
        {
            var done = 0;
            while (done < frameCount)
            {
                var count = Math.Min(_mono.Length, (int)frameCount - done);
                var mono = _mono.AsSpan(0, count);
                render(mono);
                var frames = destination.Slice(done * _channels, count * _channels);
                if (_channels == 1) mono.CopyTo(frames);
                else
                    for (var i = 0; i < count; i++)
                        for (var c = 0; c < _channels; c++)
                            frames[i * _channels + c] = mono[i];
                done += count;
            }
        }
#pragma warning disable CA1031 // An exception must never escape into native code; play silence instead.
        catch (Exception ex)
        {
            destination.Clear();
            Trace.TraceError($"Audio render failed: {ex.Message}");
        }
#pragma warning restore CA1031
        return StreamCallbackResult.Continue;
    }
}
