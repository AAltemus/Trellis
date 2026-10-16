using System.Buffers.Binary;
using System.Text;
using GuitarPractice.Audio;

namespace GuitarPractice.Audio.Tests;

public class ToneSynthTests
{
    [Fact]
    public void Ding_is_a_valid_16bit_mono_pcm_wav()
    {
        var wav = ToneSynth.DingWav(sampleRate: 48_000, seconds: 1.0);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)));      // PCM
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));      // mono
        Assert.Equal(48_000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(48_000 * 2, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        Assert.Equal(44 + 48_000 * 2, wav.Length);
    }

    [Fact]
    public void Bell_is_normalized_click_free_and_decays()
    {
        var s = ToneSynth.Bell(48_000, 880, 1.6);

        var peak = s.Max(Math.Abs);
        Assert.True(Math.Abs(peak - 0.7f) < 1e-4, $"peak {peak}");
        Assert.True(Math.Abs(s[0]) < 1e-6, "must start at silence (no click)");

        float Rms(int from, int count) => (float)Math.Sqrt(s.Skip(from).Take(count).Average(v => v * v));
        var early = Rms(0, 4800);
        var late = Rms(s.Length - 4800, 4800);
        Assert.True(late < early * 0.1, $"tail {late} should be far below attack {early}");
    }
}
