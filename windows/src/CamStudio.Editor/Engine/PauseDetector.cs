using System.Globalization;
using System.Text.RegularExpressions;

namespace CamStudio.Editor.Engine;

/// <summary>
/// Finds long pauses in speech with ffmpeg's silencedetect. The threshold sits ~10 dB above the source's
/// measured noise floor (so background noise counts as silence), or −35 dBFS when it can't be measured.
/// Each cut keeps <see cref="Padding"/> of the pause on both sides so speech never sounds clipped.
/// </summary>
public static partial class PauseDetector
{
    public const double MinPause = 0.7;
    public const double Padding = 0.2;

    /// <summary>Threshold (dBFS) for a source with the given noise floor.</summary>
    public static double ThresholdFor(double? floorDb) =>
        floorDb is double f && f > -90 ? Math.Clamp(f + 10, -60, -25) : -35;

    /// <summary>
    /// Ranges to cut, in source seconds, within [<paramref name="from"/>, <paramref name="to"/>]: every pause
    /// of at least <see cref="MinPause"/>, shrunk by <see cref="Padding"/> on each side.
    /// </summary>
    public static async Task<List<(double Start, double End)>> FindAsync(string path, double from, double to, CancellationToken ct = default)
    {
        var cuts = new List<(double, double)>();
        if (FFmpeg.FfmpegPath == null || to - from < MinPause) return cuts;
        var floor = await NoiseProfile.FloorAsync(path);
        var threshold = ThresholdFor(floor);
        var result = await FFmpeg.RunAsync(FFmpeg.FfmpegPath, new[]
        {
            "-hide_banner", "-nostats", "-loglevel", "info",
            "-ss", N(from), "-t", N(to - from), "-i", path, "-vn",
            "-af", $"silencedetect=noise={N(threshold)}dB:d={N(MinPause)}", "-f", "null", "-",
        }, ct);
        double? start = null;
        foreach (Match m in SilenceLine().Matches(result.Stderr))
        {
            var value = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (m.Groups[1].Value == "start") start = Math.Max(0, value);
            else if (start is double s)
            {
                Add(s, value);
                start = null;
            }
        }
        if (start is double open) Add(open, to - from); // a pause that runs to the end
        return cuts;

        void Add(double s, double e)
        {
            var a = from + s + Padding;
            var b = from + e - Padding;
            // Leading/trailing pauses: no speech to protect on the outer side.
            if (s <= 0.01) a = from + s;
            if (from + e >= to - 0.01) b = to;
            if (b - a >= 0.1) cuts.Add((a, b));
        }
    }

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"silence_(start|end):\s*(-?[0-9.]+)")]
    private static partial Regex SilenceLine();
}
