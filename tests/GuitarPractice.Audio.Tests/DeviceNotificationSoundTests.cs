namespace GuitarPractice.Audio.Tests;

public class DeviceNotificationSoundTests
{
    private sealed class CapturingOutput : IAudioOutput
    {
        public AudioRenderCallback? Callback { get; private set; }
        public int Starts { get; private set; }
        public IReadOnlyList<AudioDeviceInfo> Devices => [];
        public int SampleRate => 48_000;
        public TimeSpan OutputLatency => TimeSpan.Zero;
        public bool IsRunning { get; private set; }
        public int GetPreferredSampleRate(string? deviceId = null) => 48_000;

        public void Start(AudioStreamOptions options, AudioRenderCallback callback)
        {
            Callback = callback;
            IsRunning = true;
            Starts++;
        }

        public void Stop() => IsRunning = false;
        public void Dispose() => IsRunning = false;
    }

    private sealed class Devices(CapturingOutput output) : IAudioDevices
    {
        public int Created { get; private set; }
        public IAudioInput CreateInput() => throw new NotSupportedException();

        public IAudioOutput CreateOutput()
        {
            Created++;
            return output;
        }
    }

    [Fact]
    public void Ding_plays_the_bell_through_the_audio_output_on_every_platform()
    {
        var output = new CapturingOutput();
        var devices = new Devices(output);
        using var sound = new DeviceNotificationSound(devices);

        sound.PlayDing();
        Assert.True(output.IsRunning);
        var buffer = new float[48_000 / 2];
        output.Callback!(buffer);
        Assert.Equal(ToneSynth.Bell(48_000, 880, 1.6).AsSpan(0, buffer.Length).ToArray(), buffer);

        sound.PlayDing(); // again while the stream is open: same stream, the bell restarts
        output.Callback(buffer);
        Assert.Equal((1, 1), (devices.Created, output.Starts));
        Assert.Equal(ToneSynth.Bell(48_000, 880, 1.6)[0], buffer[0]);
    }

    [Fact]
    public void A_failing_device_never_throws()
    {
        using var sound = new DeviceNotificationSound(new ThrowingDevices());
        sound.PlayDing();
    }

    private sealed class ThrowingDevices : IAudioDevices
    {
        public IAudioInput CreateInput() => throw new InvalidOperationException("no device");
        public IAudioOutput CreateOutput() => throw new InvalidOperationException("no device");
    }
}
