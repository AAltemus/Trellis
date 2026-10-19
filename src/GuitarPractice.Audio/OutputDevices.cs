using System.Globalization;

namespace GuitarPractice.Audio;

/// <summary>
/// Choosing an output device by name and driver (saved across runs: PortAudio indexes change when devices come and go).
/// The same device shows once per driver, labelled with that driver's latency, so the fast one is easy to pick without
/// naming OS-specific drivers.
/// </summary>
public static class OutputDevices
{
    /// <summary>"Output 1/2 (SSL 2 USB Audio Device) · 3 ms", "(default)" on the system default.</summary>
    public static string Label(AudioDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var latency = device.LatencySeconds > 0
            ? " · " + (device.LatencySeconds * 1000).ToString("0", CultureInfo.InvariantCulture) + " ms"
            : "";
        return device.Name + latency + (device.IsDefault ? " (default)" : "");
    }

    /// <summary>Lowest latency first within each name, names alphabetical.</summary>
    public static IReadOnlyList<AudioDeviceInfo> Sorted(IEnumerable<AudioDeviceInfo> devices) =>
        [.. devices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(d => d.LatencySeconds > 0 ? d.LatencySeconds : double.MaxValue)
            .ThenBy(d => d.Driver)];

    /// <summary>The device saved as <paramref name="name"/> on <paramref name="driver"/>, if it's connected now.</summary>
    public static AudioDeviceInfo? Find(IEnumerable<AudioDeviceInfo> devices, string? name, int driver) =>
        name is null ? null : devices.FirstOrDefault(d => d.Driver == driver && string.Equals(d.Name, name, StringComparison.Ordinal));
}
