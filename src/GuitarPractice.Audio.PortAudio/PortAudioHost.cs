using System.Globalization;
using PortAudioSharp;

namespace GuitarPractice.Audio;

/// <summary>
/// Shared PortAudio lifetime (Pa_Initialize/Pa_Terminate are reference counted) and device helpers for
/// <see cref="PortAudioOutput"/> and <see cref="PortAudioInput"/>. Device ids are PortAudio indexes as strings.
/// Lists only devices of the default device's host API, so Windows doesn't show each device four times
/// (MME, DirectSound, WASAPI, WDM-KS).
/// </summary>
internal static class PortAudioHost
{
    private static readonly Lock Gate = new();
    private static int _count;

    public static void Acquire()
    {
        lock (Gate)
        {
            if (_count == 0)
            {
                PortAudio.LoadNativeLibrary();
                PortAudio.Initialize();
            }
            _count++;
        }
    }

    public static void Release()
    {
        lock (Gate)
        {
            if (_count > 0 && --_count == 0) PortAudio.Terminate();
        }
    }

    /// <param name="allDrivers">true: every host API's devices (the same device appears once per driver, each with its
    /// own latency: e.g. Windows' default driver ~90 ms, its low-latency one ~3 ms).</param>
    public static IReadOnlyList<AudioDeviceInfo> ListDevices(bool input, bool allDrivers = false)
    {
        var defaultIndex = input ? PortAudio.DefaultInputDevice : PortAudio.DefaultOutputDevice;
        var hostApi = defaultIndex == PortAudio.NoDevice ? -1 : PortAudio.GetDeviceInfo(defaultIndex).hostApi;
        var devices = new List<AudioDeviceInfo>();
        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var d = PortAudio.GetDeviceInfo(i);
            var channels = input ? d.maxInputChannels : d.maxOutputChannels;
            if (channels <= 0 || (!allDrivers && hostApi >= 0 && d.hostApi != hostApi)) continue;
            devices.Add(new AudioDeviceInfo(i.ToString(CultureInfo.InvariantCulture), d.name.Trim(), channels, d.defaultSampleRate,
                i == defaultIndex, input ? d.defaultLowInputLatency : d.defaultLowOutputLatency, d.hostApi));
        }
        return devices;
    }

    public static int ResolveDevice(string? deviceId, bool input)
    {
        if (deviceId is not null
            && int.TryParse(deviceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            && index >= 0 && index < PortAudio.DeviceCount)
        {
            var d = PortAudio.GetDeviceInfo(index);
            if ((input ? d.maxInputChannels : d.maxOutputChannels) > 0) return index;
        }
        var fallback = input ? PortAudio.DefaultInputDevice : PortAudio.DefaultOutputDevice;
        if (fallback == PortAudio.NoDevice)
            throw new InvalidOperationException(input ? "No audio input device found." : "No audio output device found.");
        return fallback;
    }

    public static int PreferredSampleRate(string? deviceId, bool input)
    {
        var rate = (int)Math.Round(PortAudio.GetDeviceInfo(ResolveDevice(deviceId, input)).defaultSampleRate);
        return rate >= 8_000 ? rate : 48_000;
    }
}
