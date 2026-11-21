using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using CamStudio.Editor.Model;

namespace CamStudio.Editor.Engine;

/// <summary>Finds and runs ffmpeg.exe / ffprobe.exe. All work happens off the UI thread.</summary>
public static class FFmpeg
{
    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".webp" };
    public static readonly string[] MediaExtensions =
        { ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".m4v", ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".wma" };

    public static string? FfmpegPath { get; private set; }
    public static string? FfprobePath { get; private set; }
    public static bool IsAvailable => FfmpegPath != null && FfprobePath != null;

    private static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CamStudioPro", "editor.json");

    private static readonly SemaphoreSlim ThumbGate = new(2);

    /// <summary>Looks next to the app (ffmpeg\ or the app folder), in the saved custom path, then on PATH.</summary>
    public static bool Locate()
    {
        FfmpegPath = FfprobePath = null;
        foreach (var dir in CandidateDirs())
        {
            var ff = Path.Combine(dir, "ffmpeg.exe");
            var fp = Path.Combine(dir, "ffprobe.exe");
            if (File.Exists(ff) && File.Exists(fp))
            {
                FfmpegPath = ff;
                FfprobePath = fp;
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<string> CandidateDirs()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "ffmpeg");
        yield return baseDir;
        var custom = LoadCustomPath();
        if (!string.IsNullOrEmpty(custom))
            yield return Path.GetDirectoryName(custom)!;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string clean;
            try { clean = dir.Trim().Trim('"'); }
            catch { continue; }
            if (clean.Length > 0)
                yield return clean;
        }
    }

    private static string? LoadCustomPath()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return null;
            return JsonNode.Parse(File.ReadAllText(SettingsFile))?["FfmpegPath"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Saves a user-chosen ffmpeg.exe (ffprobe.exe must sit next to it) and re-locates.</summary>
    public static bool SetCustomPath(string ffmpegExe)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, new JsonObject { ["FfmpegPath"] = ffmpegExe }.ToJsonString());
        }
        catch
        {
        }
        return Locate();
    }

    // ---------- process helpers ----------

    public sealed record RunResult(int ExitCode, byte[] Stdout, string Stderr)
    {
        public string StdoutText => Encoding.UTF8.GetString(Stdout);
    }

    public static async Task<RunResult> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct = default,
        Action<string>? onStdoutLine = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        process.Start();
        process.StandardInput.Close();

        var stderr = new StringBuilder();
        var errTask = Task.Run(async () =>
        {
            var buffer = new char[4096];
            int n;
            while ((n = await process.StandardError.ReadAsync(buffer, 0, buffer.Length)) > 0)
                lock (stderr)
                {
                    stderr.Append(buffer, 0, n);
                    if (stderr.Length > 64_000) stderr.Remove(0, stderr.Length - 32_000);
                }
        });

        Task<byte[]> outTask;
        if (onStdoutLine != null)
        {
            outTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync()) != null)
                    onStdoutLine(line);
                return Array.Empty<byte>();
            });
        }
        else
        {
            outTask = Task.Run(async () =>
            {
                using var ms = new MemoryStream();
                await process.StandardOutput.BaseStream.CopyToAsync(ms);
                return ms.ToArray();
            });
        }

        using (ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } }))
        {
            await process.WaitForExitAsync(CancellationToken.None);
            var output = await outTask;
            await errTask;
            ct.ThrowIfCancellationRequested();
            return new RunResult(process.ExitCode, output, stderr.ToString());
        }
    }

    private static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    // ---------- probing ----------

    public static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsSupportedFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ImageExtensions.Contains(ext) || MediaExtensions.Contains(ext);
    }

    public static async Task<MediaAsset?> ProbeAsync(string path, CancellationToken ct = default)
    {
        if (FfprobePath == null) return null;
        var result = await RunAsync(FfprobePath,
            new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path }, ct);
        if (result.ExitCode != 0) return null;

        var asset = new MediaAsset { Path = path, IsImage = IsImageFile(path) };
        try
        {
            using var doc = JsonDocument.Parse(result.Stdout);
            var root = doc.RootElement;
            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var s in streams.EnumerateArray())
                {
                    var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                    var attached = s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                    if (type == "video" && !attached && !asset.HasVideo)
                    {
                        asset.HasVideo = true;
                        asset.Width = s.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                        asset.Height = s.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                        asset.Fps = ParseRate(s.TryGetProperty("avg_frame_rate", out var r) ? r.GetString() : null);
                    }
                    else if (type == "audio")
                    {
                        asset.HasAudio = true;
                    }
                }
            }
            if (root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d) &&
                double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dur))
                asset.Duration = dur;
        }
        catch
        {
            return null;
        }

        if (asset.IsImage)
        {
            asset.HasAudio = false;
            asset.HasVideo = true;
            asset.Duration = 0;
        }
        else if (asset.Duration <= 0 || (!asset.HasVideo && !asset.HasAudio))
        {
            return null;
        }
        return asset;
    }

    private static double ParseRate(string? rate)
    {
        if (string.IsNullOrEmpty(rate)) return 0;
        var parts = rate.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dd) && dd > 0)
            return n / dd;
        return 0;
    }

    // ---------- images ----------

    /// <summary>One frame as PNG, optionally through an extra filter (used for the exact-colour preview).</summary>
    public static async Task<BitmapSource?> FrameAsync(string path, double time, int width, string? extraFilter = null,
        CancellationToken ct = default)
    {
        if (FfmpegPath == null) return null;
        var image = IsImageFile(path);
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        if (!image && time > 0) args.AddRange(new[] { "-ss", Num(time) });
        args.AddRange(new[] { "-i", path, "-frames:v", "1" });
        var vf = $"scale={width}:-2";
        if (!string.IsNullOrEmpty(extraFilter)) vf += "," + extraFilter;
        args.AddRange(new[] { "-vf", vf, "-f", "image2pipe", "-c:v", "png", "-" });

        await ThumbGate.WaitAsync(ct);
        try
        {
            var result = await RunAsync(FfmpegPath, args, ct);
            if (result.ExitCode != 0 || result.Stdout.Length == 0) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(result.Stdout);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            ThumbGate.Release();
        }
    }

    private static readonly Dictionary<string, Task<BitmapSource?>> ThumbCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A small cached thumbnail (10% in, or the image itself).</summary>
    public static Task<BitmapSource?> ThumbnailAsync(MediaAsset asset)
    {
        lock (ThumbCache)
        {
            if (!ThumbCache.TryGetValue(asset.Path, out var task))
            {
                var at = asset.IsImage ? 0 : Math.Min(asset.Duration * 0.1, 3);
                task = asset.IsAudioOnly ? Task.FromResult<BitmapSource?>(null) : Task.Run(() => FrameAsync(asset.Path, at, 192));
                ThumbCache[asset.Path] = task;
            }
            return task;
        }
    }

    // ---------- encoders ----------

    private static Task<HashSet<string>>? _encoders;

    /// <summary>Names of encoders compiled into this ffmpeg build.</summary>
    public static Task<HashSet<string>> EncodersAsync()
    {
        return _encoders ??= Task.Run(async () =>
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (FfmpegPath == null) return set;
            try
            {
                var result = await RunAsync(FfmpegPath, new[] { "-hide_banner", "-encoders" });
                foreach (var line in result.StdoutText.Split('\n'))
                {
                    var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && parts[0].Length == 6 && parts[0][0] is 'V' or 'A')
                        set.Add(parts[1]);
                }
            }
            catch
            {
            }
            return set;
        });
    }

    private static readonly Dictionary<string, Task<bool>> EncoderWorks = new();

    /// <summary>
    /// Hardware encoders are compiled into most builds even without the hardware, so try a tiny encode.
    /// </summary>
    public static Task<bool> EncoderWorksAsync(string encoder)
    {
        lock (EncoderWorks)
        {
            if (!EncoderWorks.TryGetValue(encoder, out var task))
            {
                task = Task.Run(async () =>
                {
                    if (FfmpegPath == null) return false;
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        var result = await RunAsync(FfmpegPath, new[]
                        {
                            "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=256x144:r=30:d=0.2",
                            "-pix_fmt", "yuv420p", "-c:v", encoder, "-f", "null", "-",
                        }, cts.Token);
                        return result.ExitCode == 0;
                    }
                    catch
                    {
                        return false;
                    }
                });
                EncoderWorks[encoder] = task;
            }
            return task;
        }
    }

    /// <summary>Picks a working encoder: software x264/x265 first, then GPU encoders, then Media Foundation.</summary>
    public static async Task<string?> ResolveEncoderAsync(bool hevc)
    {
        var available = await EncodersAsync();
        var candidates = hevc
            ? new[] { "libx265", "hevc_nvenc", "hevc_qsv", "hevc_amf", "hevc_mf" }
            : new[] { "libx264", "h264_nvenc", "h264_qsv", "h264_amf", "h264_mf" };
        foreach (var c in candidates)
            if (available.Contains(c) && await EncoderWorksAsync(c))
                return c;
        return null;
    }

    /// <summary>Encoders usable on this PC, for the export panel's list.</summary>
    public static async Task<List<string>> WorkingEncodersAsync()
    {
        var available = await EncodersAsync();
        var all = new[] { "libx264", "h264_nvenc", "h264_qsv", "h264_amf", "h264_mf", "libx265", "hevc_nvenc", "hevc_qsv", "hevc_amf", "hevc_mf" };
        var list = new List<string>();
        foreach (var e in all)
            if (available.Contains(e) && await EncoderWorksAsync(e))
                list.Add(e);
        return list;
    }

    public static string EncoderLabel(string encoder) => encoder switch
    {
        "libx264" => "H.264 · software (x264)",
        "libx265" => "HEVC · software (x265)",
        "h264_nvenc" => "H.264 · NVIDIA GPU",
        "hevc_nvenc" => "HEVC · NVIDIA GPU",
        "h264_qsv" => "H.264 · Intel Quick Sync",
        "hevc_qsv" => "HEVC · Intel Quick Sync",
        "h264_amf" => "H.264 · AMD GPU",
        "hevc_amf" => "HEVC · AMD GPU",
        "h264_mf" => "H.264 · Windows Media Foundation",
        "hevc_mf" => "HEVC · Windows Media Foundation",
        _ => encoder,
    };
}
