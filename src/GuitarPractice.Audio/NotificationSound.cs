using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GuitarPractice.Audio;

/// <summary>Short notification sounds (timer finished). Fire-and-forget; never throws.</summary>
public interface INotificationSound
{
    void PlayDing();
}

/// <summary>Synthesizes notification sounds as 16-bit mono PCM WAV. No assets to ship.</summary>
public static class ToneSynth
{
    // Bell-like partials: (frequency ratio, amplitude, decay per second). The 2.76x partial is the
    // inharmonic "bell" component; higher partials die quickly so the attack sparkles and the tail is pure.
    private static readonly (double Ratio, double Amp, double Decay)[] BellPartials =
    [
        (1.00, 0.60, 2.2),
        (2.00, 0.25, 3.5),
        (2.76, 0.12, 5.0),
        (5.40, 0.05, 8.0),
    ];

    public static byte[] DingWav(int sampleRate = 48_000, double fundamentalHz = 880, double seconds = 1.6)
    {
        var samples = Bell(sampleRate, fundamentalHz, seconds);
        return ToWav(samples, sampleRate);
    }

    internal static float[] Bell(int sampleRate, double fundamentalHz, double seconds)
    {
        var n = (int)(sampleRate * seconds);
        var data = new float[n];
        var attack = (int)(0.005 * sampleRate); // 5 ms ramp avoids a click
        double peak = 0;

        for (var i = 0; i < n; i++)
        {
            var t = (double)i / sampleRate;
            double v = 0;
            foreach (var (ratio, amp, decay) in BellPartials)
                v += amp * Math.Exp(-decay * t) * Math.Sin(2 * Math.PI * fundamentalHz * ratio * t);
            if (i < attack) v *= (double)i / attack;
            data[i] = (float)v;
            peak = Math.Max(peak, Math.Abs(v));
        }

        var gain = peak > 0 ? 0.7 / peak : 1;
        for (var i = 0; i < n; i++) data[i] = (float)(data[i] * gain);
        return data;
    }

    internal static byte[] ToWav(float[] samples, int sampleRate)
    {
        const short channels = 1;
        const short bitsPerSample = 16;
        var dataBytes = samples.Length * channels * bitsPerSample / 8;

        using var ms = new MemoryStream(44 + dataBytes);
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8);
            w.Write(36 + dataBytes);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);                                         // PCM fmt chunk size
            w.Write((short)1);                                   // PCM
            w.Write(channels);
            w.Write(sampleRate);
            w.Write(sampleRate * channels * bitsPerSample / 8);  // byte rate
            w.Write((short)(channels * bitsPerSample / 8));      // block align
            w.Write(bitsPerSample);
            w.Write("data"u8);
            w.Write(dataBytes);
            foreach (var s in samples)
                w.Write((short)Math.Round(Math.Clamp(s, -1f, 1f) * short.MaxValue));
        }
        return ms.ToArray();
    }
}

/// <summary>
/// Plays notification WAVs with what each OS already has, so there is no native dependency before the
/// PortAudio backend lands: winmm PlaySound on Windows, afplay on macOS, paplay/aplay on Linux.
/// </summary>
public sealed partial class SystemNotificationSound : INotificationSound
{
    private readonly Lazy<string?> _dingPath = new(() => WriteTemp("guitarpractice-ding.wav", ToneSynth.DingWav()));

    public void PlayDing()
    {
        var path = _dingPath.Value;
        if (path is null) return;

        try
        {
            if (OperatingSystem.IsWindows()) PlayWindows(path);
            else if (OperatingSystem.IsMacOS()) Launch("afplay", path);
            else if (!Launch("paplay", path)) Launch("aplay", path);
        }
#pragma warning disable CA1031 // A failed ding must never take down a practice session.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Notification sound failed: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    private static string? WriteTemp(string name, byte[] bytes)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), name);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool Launch(string player, string path)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(player, $"\"{path}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // player not installed
        }
    }

    [SupportedOSPlatform("windows")]
    private static void PlayWindows(string path)
    {
        const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_FILENAME = 0x00020000;
        PlaySound(path, IntPtr.Zero, SND_ASYNC | SND_NODEFAULT | SND_FILENAME);
    }

    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static partial bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);
}
