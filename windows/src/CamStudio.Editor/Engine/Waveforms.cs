using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CamStudio.Editor.Engine;

/// <summary>
/// Audio peaks for drawing waveforms: 100 values per second of source audio (0..1, max-abs).
/// Decoded in the background with ffmpeg (mono, 4 kHz) and cached in memory and in %TEMP%.
/// </summary>
public static class Waveforms
{
    public const int PeaksPerSecond = 100;
    private const int SampleRate = 4000;
    private const int SamplesPerPeak = SampleRate / PeaksPerSecond;

    private static readonly Dictionary<string, float[]?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Gate = new(1);

    /// <summary>Raised (on a worker thread) when a file's peaks become available.</summary>
    public static event Action<string>? Loaded;

    private static string CacheDir => Path.Combine(Path.GetTempPath(), "CamStudioEditor", "waves");

    /// <summary>Peaks for <paramref name="path"/>, or null while they load (loading starts on first call).</summary>
    public static float[]? Get(string path)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var peaks)) return peaks;
            if (!Pending.Add(path)) return null;
        }
        _ = Task.Run(() => LoadAsync(path));
        return null;
    }

    private static async Task LoadAsync(string path)
    {
        float[]? peaks = null;
        try
        {
            var cacheFile = CacheFile(path);
            if (cacheFile != null && File.Exists(cacheFile))
                peaks = ReadCache(cacheFile);
            if (peaks == null && FFmpeg.FfmpegPath != null && File.Exists(path))
            {
                await Gate.WaitAsync();
                try
                {
                    var result = await FFmpeg.RunAsync(FFmpeg.FfmpegPath,
                        new[] { "-v", "error", "-i", path, "-vn", "-ac", "1", "-ar", SampleRate.ToString(), "-f", "s16le", "-" });
                    if (result.ExitCode == 0)
                        peaks = Compute(result.Stdout);
                }
                finally
                {
                    Gate.Release();
                }
                if (peaks != null && cacheFile != null)
                    WriteCache(cacheFile, peaks);
            }
        }
        catch
        {
            peaks = null;
        }
        lock (Cache)
        {
            Cache[path] = peaks ?? Array.Empty<float>();
            Pending.Remove(path);
        }
        Loaded?.Invoke(path);
    }

    public static float[] Compute(byte[] pcm)
    {
        var samples = pcm.Length / 2;
        var count = (samples + SamplesPerPeak - 1) / SamplesPerPeak;
        var peaks = new float[count];
        for (var i = 0; i < count; i++)
        {
            var max = 0;
            var end = Math.Min(samples, (i + 1) * SamplesPerPeak);
            for (var s = i * SamplesPerPeak; s < end; s++)
            {
                var v = Math.Abs((int)(short)(pcm[2 * s] | pcm[2 * s + 1] << 8));
                if (v > max) max = v;
            }
            peaks[i] = Math.Min(1f, max / 32768f);
        }
        return peaks;
    }

    private static string? CacheFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var key = $"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|v1";
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
            return Path.Combine(CacheDir, hash + ".peaks");
        }
        catch
        {
            return null;
        }
    }

    private static float[]? ReadCache(string file)
    {
        try
        {
            var bytes = File.ReadAllBytes(file);
            var peaks = new float[bytes.Length];
            for (var i = 0; i < bytes.Length; i++) peaks[i] = bytes[i] / 255f;
            return peaks;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteCache(string file, float[] peaks)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var bytes = new byte[peaks.Length];
            for (var i = 0; i < peaks.Length; i++) bytes[i] = (byte)Math.Round(Math.Clamp(peaks[i], 0, 1) * 255);
            File.WriteAllBytes(file, bytes);
        }
        catch
        {
        }
    }
}
