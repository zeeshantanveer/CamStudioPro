using System.Globalization;
using System.Text.Json;
using CamStudio.Editor.Model;

namespace CamStudio.Editor.Engine;

/// <summary>EBU R128 loudness measurement with ffmpeg's loudnorm analysis, and the verdict shown to the user.</summary>
public static class Loudness
{
    /// <summary>Measures a finished file (any media with an audio stream). Null when it has no audio.</summary>
    public static async Task<LoudnessReading?> MeasureFileAsync(string path, CancellationToken ct = default)
    {
        if (FFmpeg.FfmpegPath == null) return null;
        try
        {
            var result = await FFmpeg.RunAsync(FFmpeg.FfmpegPath, new[]
            {
                "-hide_banner", "-nostats", "-loglevel", "info", "-i", path, "-vn",
                "-af", "loudnorm=I=-14:TP=-1:LRA=11:print_format=json", "-f", "null", "-",
            }, ct);
            return result.ExitCode == 0 ? ParseLoudnorm(result.Stderr) : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads the input_* values from loudnorm's JSON report (the last {...} block in stderr).</summary>
    public static LoudnessReading? ParseLoudnorm(string stderr)
    {
        var end = stderr.LastIndexOf('}');
        var start = end < 0 ? -1 : stderr.LastIndexOf('{', end);
        if (start < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(stderr[start..(end + 1)]);
            var root = doc.RootElement;
            double Get(string name) =>
                root.TryGetProperty(name, out var v) && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
            var reading = new LoudnessReading(Get("input_i"), Get("input_tp"), Get("input_lra"), Get("input_thresh"), Get("target_offset"));
            if (!double.IsFinite(reading.Integrated) || reading.Integrated < -70) return null; // silence
            return reading with { Offset = double.IsFinite(reading.Offset) ? reading.Offset : 0 };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>One line for the user: is this ready for the chosen platform?</summary>
    public static string Verdict(LoudnessReading r, LoudnessTarget target)
    {
        if (target != LoudnessTarget.Off)
        {
            var (goal, _) = ExportBuilder.TargetFor(target);
            return Math.Abs(r.Integrated - goal) <= 1
                ? $"✓ Already at {ExportBuilder.TargetLabel(target)}."
                : $"Export will set it to {ExportBuilder.TargetLabel(target)}.";
        }
        var youtube = ExportBuilder.TargetFor(LoudnessTarget.YouTube).Integrated;
        var diff = r.Integrated - youtube;
        if (Math.Abs(diff) <= 1) return "✓ Ready for YouTube (−14 LUFS).";
        return diff < 0
            ? $"Too quiet by {-diff:0} LU for YouTube. Set a loudness target."
            : $"Too loud by {diff:0} LU for YouTube. Set a loudness target.";
    }
}
