namespace GuitarPractice.Audio.Tests;

public class OutputDevicesTests
{
    private static readonly AudioDeviceInfo[] Devices =
    [
        new("7", "Output 1/2 (SSL 2 USB Audio Device)", 2, 44_100, IsDefault: true, LatencySeconds: 0.090, Driver: 0),
        new("16", "Output 1/2 (SSL 2 USB Audio Device)", 2, 44_100, IsDefault: false, LatencySeconds: 0.120, Driver: 1),
        new("18", "Output 1/2 (SSL 2 USB Audio Device)", 2, 44_100, IsDefault: false, LatencySeconds: 0.003, Driver: 2),
        new("8", "Headphones", 2, 48_000, IsDefault: false, LatencySeconds: 0, Driver: 0),
    ];

    [Fact]
    public void Labels_show_the_latency_and_the_default()
    {
        Assert.Equal("Output 1/2 (SSL 2 USB Audio Device) · 90 ms (default)", OutputDevices.Label(Devices[0]));
        Assert.Equal("Output 1/2 (SSL 2 USB Audio Device) · 3 ms", OutputDevices.Label(Devices[2]));
        Assert.Equal("Headphones", OutputDevices.Label(Devices[3]));                // latency unknown
    }

    [Fact]
    public void Sorted_by_name_then_fastest_first()
    {
        Assert.Equal(["8", "18", "7", "16"], OutputDevices.Sorted(Devices).Select(d => d.Id));
    }

    [Fact]
    public void A_saved_choice_is_found_by_name_and_driver_not_by_index()
    {
        var replugged = Devices.Select(d => d with { Id = d.Id + "0" }).ToArray();  // indexes change between runs
        Assert.Equal("180", OutputDevices.Find(replugged, "Output 1/2 (SSL 2 USB Audio Device)", 2)?.Id);
        Assert.Null(OutputDevices.Find(replugged, "Output 1/2 (SSL 2 USB Audio Device)", 5)); // that driver is gone
        Assert.Null(OutputDevices.Find(replugged, "Unplugged interface", 2));
        Assert.Null(OutputDevices.Find(replugged, null, -1));                       // nothing saved = default
    }
}
