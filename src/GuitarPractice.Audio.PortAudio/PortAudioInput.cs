using System.Diagnostics;
using PortAudioSharp;
using PaStream = PortAudioSharp.Stream;

namespace GuitarPractice.Audio;

/// <summary>
/// <see cref="IAudioInput"/> on PortAudio. Captures up to 2 channels and hands the callback mono: the chosen channel
/// (<see cref="AudioStreamOptions.Channel"/> = 1 or 2, e.g. the guitar input on a 2-in interface) or the average of
/// both (0). The PortAudio callback copies into a pre-allocated scratch buffer and calls the capture callback; no
/// allocation or locks.
/// </summary>
public sealed class PortAudioInput : IAudioInput
{
    private readonly PaStream.Callback _paCallback; // kept in a field so the GC never collects the native thunk
    private PaStream? _stream;
    private AudioCaptureCallback? _capture;
    private float[] _mono = [];
    private int _channels;
    private int _channel;
    private bool _initialized;

    public PortAudioInput()
    {
        _paCallback = OnAudio;
    }

    public IReadOnlyList<AudioDeviceInfo> Devices
    {
        get
        {
            EnsureInitialized();
            return PortAudioHost.ListDevices(input: true);
        }
    }

    public int SampleRate { get; private set; }

    public bool IsRunning => _stream is { IsActive: true };

    /// <summary>Name of the device the stream is open on, for display.</summary>
    public string? DeviceName { get; private set; }

    /// <summary>Input channels opened (1 or 2).</summary>
    public int ChannelCount => _channels;

    public int GetPreferredSampleRate(string? deviceId = null)
    {
        EnsureInitialized();
        return PortAudioHost.PreferredSampleRate(deviceId, input: true);
    }

    public void Start(AudioStreamOptions options, AudioCaptureCallback callback)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(callback);
        Stop();
        EnsureInitialized();

        var device = PortAudioHost.ResolveDevice(options.DeviceId, input: true);
        var info = PortAudio.GetDeviceInfo(device);
        _channels = Math.Min(2, Math.Max(1, info.maxInputChannels));
        _channel = options.Channel >= 1 && options.Channel <= _channels ? options.Channel : 0;
        _mono = new float[Math.Max(options.FramesPerBuffer, 4_096)];
        _capture = callback;

        var parameters = new StreamParameters
        {
            device = device,
            channelCount = _channels,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = info.defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        _stream = new PaStream(
            inParams: parameters,
            outParams: null,
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
        var capture = _capture;
        if (capture is null || input == IntPtr.Zero) return StreamCallbackResult.Continue;

        try
        {
            var source = new ReadOnlySpan<float>((void*)input, (int)frameCount * _channels);
            var done = 0;
            while (done < frameCount)
            {
                var count = Math.Min(_mono.Length, (int)frameCount - done);
                var mono = _mono.AsSpan(0, count);
                var frames = source.Slice(done * _channels, count * _channels);
                if (_channels == 1) frames.CopyTo(mono);
                else if (_channel == 0)
                    for (var i = 0; i < count; i++) mono[i] = (frames[2 * i] + frames[2 * i + 1]) * 0.5f;
                else
                    for (var i = 0; i < count; i++) mono[i] = frames[2 * i + _channel - 1];
                capture(mono);
                done += count;
            }
        }
#pragma warning disable CA1031 // An exception must never escape into native code.
        catch (Exception ex)
        {
            Trace.TraceError($"Audio capture failed: {ex.Message}");
        }
#pragma warning restore CA1031
        return StreamCallbackResult.Continue;
    }
}
