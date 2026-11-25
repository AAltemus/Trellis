namespace GuitarPractice.Audio;

/// <summary>PortAudio-backed <see cref="IAudioDevices"/> (desktop).</summary>
public sealed class PortAudioDevices : IAudioDevices
{
    public IAudioInput CreateInput() => new PortAudioInput();
    public IAudioOutput CreateOutput() => new PortAudioOutput();
    public IAudioDuplex CreateDuplex() => new PortAudioDuplex();
}
