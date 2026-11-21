using System.Globalization;

namespace CamStudio.Editor.Engine;

/// <summary>
/// Measures a source's background-noise level so noise reduction can be aimed at it. ffmpeg's afftdn
/// needs its noise floor (nf) close to the real noise to work well, and its adaptive tracking (tn=1)
/// barely reduces anything, so instead the floor is measured: the quiet end (10th percentile) of 50 ms
/// RMS windows over the whole file. Cached per path; runs once in the background.
/// </summary>
public static class NoiseProfile
{
    /// <summary>Used when a file can't be analysed.</summary>
    public const double DefaultFloorDb = -50;

    private static readonly Dictionary<string, Task<double>> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Noise floor of <paramref name="path"/> in dBFS (RMS).</summary>
    public static Task<double> FloorAsync(string path)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(path, out var task))
            {
                task = Task.Run(() => MeasureAsync(path));
                Cache[path] = task;
            }
            return task;
        }
    }

    /// <summary>The floor if it has been measured already, otherwise null (and measuring starts).</summary>
    public static double? TryGet(string path)
    {
        var task = FloorAsync(path);
        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    private static async Task<double> MeasureAsync(string path)
    {
        if (FFmpeg.FfmpegPath == null) return DefaultFloorDb;
        try
        {
            var result = await FFmpeg.RunAsync(FFmpeg.FfmpegPath, new[]
            {
                "-v", "error", "-i", path, "-vn", "-ac", "1",
                "-af", "aresample=48000,asetnsamples=n=2400:p=0,astats=metadata=1:reset=1,ametadata=mode=print:key=lavfi.astats.Overall.RMS_level:file=-",
                "-f", "null", "-",
            });
            var levels = new List<double>();
            foreach (var line in result.StdoutText.Split('\n'))
            {
                var i = line.IndexOf("RMS_level=", StringComparison.Ordinal);
                if (i < 0) continue;
                if (double.TryParse(line[(i + 10)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var db) &&
                    double.IsFinite(db) && db > -120)
                    levels.Add(db);
            }
            if (levels.Count < 4) return DefaultFloorDb;
            levels.Sort();
            return levels[(int)(levels.Count * 0.10)];
        }
        catch
        {
            return DefaultFloorDb;
        }
    }

    /// <summary>afftdn's noise-floor setting for a measured floor: a few dB above it, within afftdn's range.</summary>
    public static double AfftdnFloor(double measuredDb) => Math.Clamp(Math.Round(measuredDb + 3), -80, -20);
}
