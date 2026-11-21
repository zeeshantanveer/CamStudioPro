using System.Globalization;
using System.IO;
using System.Text;
using CamStudio.Editor.Model;

namespace CamStudio.Editor.Engine;

public enum ExportQuality { High, Standard, Small }

/// <summary>Audio-only export formats.</summary>
public enum AudioFormat { Aac, Mp3, Wav }

/// <param name="Audio">Non-null: export only the mixed audio in this format (Width/Height/Fps/Encoder are ignored).</param>
public sealed record ExportOptions(
    int Width,
    int Height,
    double Fps,
    ExportQuality Quality,
    string Encoder,
    string OutputPath,
    AudioFormat? Audio = null);

public sealed record ExportProgress(double Fraction, TimeSpan? Remaining, string? Stage = null);

/// <summary>EBU R128 measurement (loudnorm/ebur128): integrated LUFS, true peak dBTP, loudness range LU.</summary>
public sealed record LoudnessReading(double Integrated, double TruePeak, double Range, double Threshold, double Offset)
{
    public override string ToString() => $"{Integrated:0.0} LUFS · peak {TruePeak:0.0} dBTP · range {Range:0.0} LU";
}

/// <summary>What an export produced: the measured loudness of the written file (null if it couldn't be measured).</summary>
public sealed record ExportResult(LoudnessReading? Loudness);

/// <summary>
/// Turns an <see cref="EditorProject"/> into one ffmpeg command: every V1 clip is normalised to the
/// output size/rate, concatenated with its audio, titles are burned in with drawtext, and A2 music
/// is mixed on top.
/// </summary>
public static class ExportBuilder
{
    public const string FontFile = @"C:\Windows\Fonts\segoeuib.ttf";

    private static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Path for use inside a filtergraph option value: forward slashes, escaped colon, quoted.</summary>
    public static string FilterPath(string path) => "'" + path.Replace('\\', '/').Replace(":", "\\:").Replace("'", "") + "'";

    /// <summary>The colour adjustment part of a clip's chain, or null when the clip is untouched.</summary>
    public static string? ColorFilter(VideoClip clip) => clip.HasColor
        ? $"eq=brightness={N(clip.Brightness)}:contrast={N(clip.Contrast)}:saturation={N(clip.Saturation)}"
        : null;

    /// <summary>
    /// The audio clean-up part of a clip's chain (hum → noise reduction → loudness), or null when off.
    /// Runs on the source audio, before speed changes and resampling.
    /// </summary>
    public static string? CleanupFilter(IAudioClip clip) =>
        VoiceChain.Build(AudioCleanup.From(clip), clip.NoiseReduction == NoiseLevel.Off ? null : NoiseProfile.TryGet(clip.Path));

    /// <param name="noiseFloorDb">Measured noise floor of the source (see <see cref="NoiseProfile"/>); null = typical default.</param>
    public static string? CleanupFilter(NoiseLevel noise, bool removeHum, bool normalize, double? noiseFloorDb) =>
        VoiceChain.Build(new AudioCleanup(noise, removeHum, normalize, false, VoiceStyle.Natural), noiseFloorDb);

    /// <summary>Integrated loudness and true-peak targets for a project setting.</summary>
    public static (double Integrated, double TruePeak) TargetFor(LoudnessTarget t) => t switch
    {
        LoudnessTarget.Podcast => (-16, -1),
        _ => (-14, -1),
    };

    public static string TargetLabel(LoudnessTarget t) => t switch
    {
        LoudnessTarget.Podcast => "Podcast (−16 LUFS)",
        LoudnessTarget.YouTube => "YouTube (−14 LUFS)",
        _ => "Off",
    };

    /// <summary>Music ducking strength: compressor threshold (dBFS of the voice) and ratio.</summary>
    private static (double ThresholdDb, double Ratio) DuckNumbers(DuckAmount a) => a switch
    {
        DuckAmount.Light => (-34, 4),
        DuckAmount.Strong => (-46, 20),
        _ => (-40, 10),
    };

    /// <summary>Measures the noise floor of every source that has noise reduction on (before building a command).</summary>
    public static async Task PrepareAsync(EditorProject project)
    {
        var paths = project.Video.Cast<IAudioClip>().Concat(project.Music)
            .Where(c => c.NoiseReduction != NoiseLevel.Off && !c.Muted)
            .Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase);
        await Task.WhenAll(paths.Select(NoiseProfile.FloorAsync));
    }

    /// <summary>Audio-only file extension for a format.</summary>
    public static string Extension(AudioFormat f) => f switch { AudioFormat.Mp3 => ".mp3", AudioFormat.Wav => ".wav", _ => ".m4a" };

    public static IEnumerable<string> AudioEncoderArgs(AudioFormat format, ExportQuality q)
    {
        var kbps = q switch { ExportQuality.High => 256, ExportQuality.Small => 128, _ => 192 };
        return format switch
        {
            AudioFormat.Mp3 => new[] { "-c:a", "libmp3lame", "-b:a", $"{kbps}k" },
            AudioFormat.Wav => new[] { "-c:a", "pcm_s16le" },
            _ => new[] { "-c:a", "aac", "-b:a", $"{kbps}k" },
        };
    }

    public static string AtempoChain(double speed)
    {
        var parts = new List<string>();
        var s = speed;
        while (s > 2.0 + 1e-9) { parts.Add("atempo=2"); s /= 2; }
        while (s < 0.5 - 1e-9) { parts.Add("atempo=0.5"); s /= 0.5; }
        if (Math.Abs(s - 1) > 1e-6) parts.Add($"atempo={N(s)}");
        return string.Join(",", parts);
    }

    public sealed class Command : IDisposable
    {
        public List<string> Args { get; } = new();
        public List<string> TempFiles { get; } = new();
        public string Graph { get; set; } = "";
        public double Duration { get; set; }

        public void Dispose()
        {
            foreach (var f in TempFiles)
                try { File.Delete(f); } catch { }
        }
    }

    /// <param name="measured">Second loudness pass: the first pass's reading of this mix (applies the target linearly).</param>
    /// <param name="measureOnly">First loudness pass: renders only the audio mix to measure it (output discarded).</param>
    public static Command Build(EditorProject project, ExportOptions o, LoudnessReading? measured = null, bool measureOnly = false)
    {
        project.Layout();
        if (measureOnly) o = o with { Audio = AudioFormat.Wav };
        var audioOnly = o.Audio != null;
        if (!audioOnly && project.Video.Count == 0)
            throw new InvalidOperationException("Add at least one clip to the video track.");
        if (!project.CanExport)
            throw new InvalidOperationException("Add a clip or some audio first.");

        var cmd = new Command { Duration = project.PlayLength };
        int W = o.Width, H = o.Height;
        var fps = o.Fps <= 0 ? 30 : o.Fps;
        var args = cmd.Args;
        // The first loudness pass needs loudnorm's report, which is printed at "info" level.
        args.AddRange(new[] { "-hide_banner", "-y", "-nostats", "-progress", "pipe:1", "-loglevel", measureOnly ? "info" : "error" });

        var graph = new StringBuilder();
        var input = 0;   // ffmpeg input index
        var segment = 0; // V1 clip index
        var concat = new StringBuilder();

        foreach (var clip in project.Video)
        {
            var D = clip.Duration;
            // Audio-only exports skip still images entirely (they contribute silence).
            var hasInput = !(audioOnly && clip.IsImage);
            if (clip.IsImage)
            {
                if (hasInput)
                    args.AddRange(new[] { "-loop", "1", "-framerate", N(fps), "-t", N(D + 0.5), "-i", clip.Path });
            }
            else
            {
                args.AddRange(new[] { "-ss", N(clip.In), "-t", N(clip.Out - clip.In), "-i", clip.Path });
            }

            if (!audioOnly)
            {
                var v = new List<string>
                {
                    $"setpts=(PTS-STARTPTS)/{N(clip.Speed)}",
                    $"scale={W}:{H}:force_original_aspect_ratio=decrease",
                    $"pad={W}:{H}:(ow-iw)/2:(oh-ih)/2:color=black",
                    "setsar=1",
                    $"fps={N(fps)}",
                    "format=yuv420p",
                };
                if (ColorFilter(clip) is { } eq) v.Add(eq);
                v.Add($"tpad=stop_mode=clone:stop_duration={N(D)}");
                v.Add($"trim=duration={N(D)}");
                v.Add("setpts=PTS-STARTPTS");
                if (clip.FadeIn > 0.01) v.Add($"fade=t=in:st=0:d={N(Math.Min(clip.FadeIn, D))}");
                if (clip.FadeOut > 0.01) v.Add($"fade=t=out:st={N(Math.Max(0, D - clip.FadeOut))}:d={N(Math.Min(clip.FadeOut, D))}");
                graph.Append($"[{input}:v]{string.Join(",", v)}[v{input}];\n");
            }

            var label = $"a{segment}";
            if (clip.PlaysAudio && hasInput)
            {
                var a = new List<string> { "asetpts=PTS-STARTPTS" };
                if (CleanupFilter(clip) is { } clean) a.Add(clean);
                var tempo = AtempoChain(clip.Speed);
                if (tempo.Length > 0) a.Add(tempo);
                a.Add("aresample=48000");
                a.Add("aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo");
                if (Math.Abs(clip.Volume - 1) > 0.001) a.Add($"volume={N(clip.Volume)}");
                a.Add("apad");
                a.Add($"atrim=duration={N(D)}");
                a.Add("asetpts=PTS-STARTPTS");
                if (clip.FadeIn > 0.01) a.Add($"afade=t=in:st=0:d={N(Math.Min(clip.FadeIn, D))}");
                if (clip.FadeOut > 0.01) a.Add($"afade=t=out:st={N(Math.Max(0, D - clip.FadeOut))}:d={N(Math.Min(clip.FadeOut, D))}");
                graph.Append($"[{input}:a]{string.Join(",", a)}[{label}];\n");
            }
            else
            {
                graph.Append($"anullsrc=r=48000:cl=stereo,atrim=duration={N(D)},aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo[{label}];\n");
            }
            concat.Append(audioOnly ? $"[{label}]" : $"[v{input}][{label}]");
            if (hasInput) input++;
            segment++;
        }

        if (project.Video.Count > 0)
        {
            graph.Append(audioOnly
                ? $"{concat}concat=n={project.Video.Count}:v=0:a=1[acat];\n"
                : $"{concat}concat=n={project.Video.Count}:v=1:a=1[vcat][acat];\n");
        }
        else
        {
            // Audio-only project: silence for the full length; music is mixed on top.
            graph.Append($"anullsrc=r=48000:cl=stereo,atrim=duration={N(cmd.Duration)},aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo[acat];\n");
        }

        // titles
        var videoOut = "vcat";
        var titles = audioOnly ? new List<TitleClip>()
            : project.Titles.Where(t => t.Start < cmd.Duration && !string.IsNullOrWhiteSpace(t.Text)).OrderBy(t => t.Start).ToList();
        if (titles.Count > 0)
        {
            var draws = new List<string>();
            var scale = H / 1080.0;
            foreach (var t in titles)
            {
                var textFile = Path.Combine(Path.GetTempPath(), $"camstudio-title-{Guid.NewGuid():N}.txt");
                File.WriteAllText(textFile, t.Text.Replace("\r\n", "\n"), new UTF8Encoding(false));
                cmd.TempFiles.Add(textFile);
                draws.Add(DrawText(t, textFile, scale));
            }
            graph.Append($"[vcat]{string.Join(",", draws)}[vtit];\n");
            videoOut = "vtit";
        }

        // music
        var audioOut = "acat";
        var music = project.Music.Where(m => m.Start < cmd.Duration && m.PlaysAudio).ToList();
        if (music.Count > 0)
        {
            // Ducking: the music is compressed by the voice (the video track's sound) as a side chain.
            var duck = project.DuckMusic && project.Video.Any(v => v.PlaysAudio && !(audioOnly && v.IsImage));
            var mixInputs = new StringBuilder(duck ? "" : "[acat]");
            var k = 0;
            foreach (var m in music)
            {
                args.AddRange(new[] { "-ss", N(m.In), "-t", N(m.Out - m.In), "-i", m.Path });
                var D = m.Duration;
                var ms = (long)Math.Round(m.Start * 1000);
                var a = new List<string> { "asetpts=PTS-STARTPTS" };
                if (CleanupFilter(m) is { } clean) a.Add(clean);
                a.Add("aresample=48000");
                a.Add("aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo");
                a.Add($"volume={N(m.Volume)}");
                a.Add($"atrim=duration={N(D)}");
                if (m.FadeIn > 0.01) a.Add($"afade=t=in:st=0:d={N(Math.Min(m.FadeIn, D))}");
                if (m.FadeOut > 0.01) a.Add($"afade=t=out:st={N(Math.Max(0, D - m.FadeOut))}:d={N(Math.Min(m.FadeOut, D))}");
                if (ms > 0) a.Add($"adelay={ms}:all=1");
                graph.Append($"[{input}:a]{string.Join(",", a)}[m{k}];\n");
                mixInputs.Append($"[m{k}]");
                input++;
                k++;
            }
            if (duck)
            {
                var (thr, ratio) = DuckNumbers(project.DuckAmount);
                graph.Append("[acat]asplit=2[voice][vsc];\n");
                graph.Append(music.Count == 1 ? "" : $"{mixInputs}amix=inputs={music.Count}:duration=longest:dropout_transition=0:normalize=0[mall];\n");
                var musicLabel = music.Count == 1 ? "m0" : "mall";
                graph.Append($"[{musicLabel}][vsc]sidechaincompress=threshold={N(Math.Pow(10, thr / 20))}:ratio={N(ratio)}:attack=20:release=600:knee=6[mduck];\n");
                graph.Append("[voice][mduck]amix=inputs=2:duration=first:dropout_transition=0:normalize=0,alimiter=limit=0.97[amix];\n");
            }
            else
            {
                graph.Append($"{mixInputs}amix=inputs={music.Count + 1}:duration=first:dropout_transition=0:normalize=0,alimiter=limit=0.97[amix];\n");
            }
            audioOut = "amix";
        }

        // Loudness target on the final mix (two passes: measure, then apply linearly).
        if (project.LoudnessTarget != LoudnessTarget.Off && (measureOnly || measured != null))
        {
            var (I, TP) = TargetFor(project.LoudnessTarget);
            var ln = $"loudnorm=I={N(I)}:TP={N(TP)}:LRA=11";
            ln += measureOnly
                ? ":print_format=json"
                : $":measured_I={N(measured!.Integrated)}:measured_TP={N(measured.TruePeak)}:measured_LRA={N(measured.Range)}" +
                  $":measured_thresh={N(measured.Threshold)}:offset={N(measured.Offset)}:linear=true:print_format=summary";
            graph.Append($"[{audioOut}]{ln},aresample=48000[aloud];\n");
            audioOut = "aloud";
        }
        else if (measureOnly)
        {
            graph.Append($"[{audioOut}]loudnorm=I=-14:TP=-1:LRA=11:print_format=json,aresample=48000[aloud];\n");
            audioOut = "aloud";
        }

        cmd.Graph = graph.ToString().TrimEnd(';', '\n');
        var graphFile = Path.Combine(Path.GetTempPath(), $"camstudio-graph-{Guid.NewGuid():N}.txt");
        File.WriteAllText(graphFile, cmd.Graph, new UTF8Encoding(false));
        cmd.TempFiles.Add(graphFile);

        args.AddRange(new[] { "-/filter_complex", graphFile });
        if (measureOnly)
        {
            args.AddRange(new[] { "-map", $"[{audioOut}]", "-vn", "-t", N(cmd.Duration), "-f", "null", "-" });
            return cmd;
        }
        if (o.Audio is { } format)
        {
            args.AddRange(new[] { "-map", $"[{audioOut}]", "-vn" });
            args.AddRange(AudioEncoderArgs(format, o.Quality));
            args.AddRange(new[] { "-ar", "48000", "-ac", "2", "-t", N(cmd.Duration) });
            if (format == AudioFormat.Aac) args.AddRange(new[] { "-movflags", "+faststart" });
            args.Add(o.OutputPath);
            return cmd;
        }
        args.AddRange(new[] { "-map", $"[{videoOut}]", "-map", $"[{audioOut}]" });
        args.AddRange(EncoderArgs(o.Encoder, o.Quality, W, H, fps));
        args.AddRange(new[] { "-r", N(fps), "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2",
            "-t", N(cmd.Duration), "-movflags", "+faststart", o.OutputPath });
        return cmd;
    }

    private static string DrawText(TitleClip t, string textFile, double scale)
    {
        var size = Math.Max(8, (int)Math.Round(t.FontSize * scale));
        var a = t.Start;
        var b = t.End;
        var fade = Math.Min(0.3, t.Duration / 3);
        var color = t.Color switch
        {
            TitleColor.Black => "0x111111",
            TitleColor.Accent => "0xFFB020",
            _ => "0xFFFFFF",
        };
        var boxColor = t.Color == TitleColor.Black ? "white@0.8" : "black@0.55";
        var (x, y) = t.Position switch
        {
            TitlePosition.Top => ("(w-text_w)/2", "h*0.08"),
            TitlePosition.Center => ("(w-text_w)/2", "(h-text_h)/2"),
            TitlePosition.LowerThird => ("w*0.06", "h*0.72"),
            _ => ("(w-text_w)/2", "h-text_h-h*0.08"),
        };
        var parts = new List<string>
        {
            $"fontfile={FilterPath(FontFile)}",
            $"textfile={FilterPath(textFile)}",
            "expansion=none",
            $"fontsize={size}",
            $"fontcolor={color}",
            "line_spacing=" + (int)Math.Round(size * 0.2),
            $"x={x}",
            $"y={y}",
            // Inside '...' the commas are protected from the filtergraph parser; no further escaping needed.
            $"alpha='if(lt(t,{N(a + fade)}),(t-{N(a)})/{N(fade)},if(gt(t,{N(b - fade)}),({N(b)}-t)/{N(fade)},1))'",
            $"enable='between(t,{N(a)},{N(b)})'",
        };
        if (t.Box)
        {
            parts.Add("box=1");
            parts.Add($"boxcolor={boxColor}");
            parts.Add($"boxborderw={(int)Math.Round(size * 0.35)}");
        }
        return "drawtext=" + string.Join(":", parts);
    }

    public static IEnumerable<string> EncoderArgs(string encoder, ExportQuality q, int w, int h, double fps)
    {
        int Pick(int high, int std, int small) => q switch { ExportQuality.High => high, ExportQuality.Small => small, _ => std };
        // Bitrate for encoders that only take a rate: bits per pixel per frame.
        var bpp = q switch { ExportQuality.High => 0.12, ExportQuality.Small => 0.045, _ => 0.075 };
        var hevc = encoder.Contains("265") || encoder.StartsWith("hevc");
        var kbps = (long)(w * (double)h * fps * bpp * (hevc ? 0.7 : 1) / 1000);
        var rate = $"{Math.Max(500, kbps)}k";

        switch (encoder)
        {
            case "libx264":
                return new[] { "-c:v", "libx264", "-preset", "medium", "-crf", Pick(18, 22, 27).ToString(), "-pix_fmt", "yuv420p", "-profile:v", "high" };
            case "libx265":
                return new[] { "-c:v", "libx265", "-preset", "medium", "-crf", Pick(20, 24, 29).ToString(), "-pix_fmt", "yuv420p", "-tag:v", "hvc1" };
            case "h264_nvenc":
            case "hevc_nvenc":
                return new[] { "-c:v", encoder, "-preset", "p5", "-rc", "vbr", "-cq", Pick(19, 24, 30).ToString(), "-b:v", "0", "-pix_fmt", "yuv420p" }
                    .Concat(hevc ? new[] { "-tag:v", "hvc1" } : Array.Empty<string>());
            case "h264_qsv":
            case "hevc_qsv":
                return new[] { "-c:v", encoder, "-global_quality", Pick(20, 25, 31).ToString() }
                    .Concat(hevc ? new[] { "-tag:v", "hvc1" } : Array.Empty<string>());
            case "h264_amf":
            case "hevc_amf":
                return new[] { "-c:v", encoder, "-rc", "cqp", "-qp_i", Pick(19, 24, 30).ToString(), "-qp_p", Pick(21, 26, 32).ToString() }
                    .Concat(hevc ? new[] { "-tag:v", "hvc1" } : Array.Empty<string>());
            default: // h264_mf / hevc_mf and anything else: bitrate control
                return new[] { "-c:v", encoder, "-b:v", rate, "-pix_fmt", "nv12" }
                    .Concat(hevc ? new[] { "-tag:v", "hvc1" } : Array.Empty<string>());
        }
    }

    /// <summary>
    /// Runs the export. With a loudness target, the audio mix is measured first (fast, audio only) and the
    /// target is then applied in the real export. Afterwards the written file's loudness is measured.
    /// Deletes the partial output on failure or cancel.
    /// </summary>
    public static async Task<ExportResult> ExportAsync(EditorProject project, ExportOptions o, IProgress<ExportProgress>? progress,
        CancellationToken ct)
    {
        if (FFmpeg.FfmpegPath == null)
            throw new InvalidOperationException("ffmpeg.exe was not found.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.OutputPath))!);
        await PrepareAsync(project);
        ct.ThrowIfCancellationRequested();

        LoudnessReading? measured = null;
        var twoPass = project.LoudnessTarget != LoudnessTarget.Off;
        // Share of the progress bar for the measuring pass (audio only renders much faster than video).
        var share = !twoPass ? 0 : o.Audio != null ? 0.45 : 0.15;
        if (twoPass)
        {
            measured = await MeasureMixAsync(project, o, new Progress<double>(f =>
                progress?.Report(new ExportProgress(f * share, null, "Measuring loudness…"))), ct);
            if (measured == null)
                throw new InvalidOperationException("Couldn't measure the loudness of the mix.");
        }

        using var cmd = Build(project, o, measured);
        var started = DateTime.UtcNow;
        var total = Math.Max(0.01, cmd.Duration);
        void OnLine(string line)
        {
            if (ParseProgress(line, total) is not { } fraction) return;
            TimeSpan? remaining = null;
            var elapsed = DateTime.UtcNow - started;
            if (fraction > 0.02 && elapsed.TotalSeconds > 1)
                remaining = TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - fraction) / fraction);
            progress?.Report(new ExportProgress(share + fraction * (1 - share), remaining));
        }

        FFmpeg.RunResult result;
        try
        {
            result = await FFmpeg.RunAsync(FFmpeg.FfmpegPath, cmd.Args, ct, OnLine);
        }
        catch
        {
            TryDelete(o.OutputPath);
            throw;
        }
        if (result.ExitCode != 0)
        {
            TryDelete(o.OutputPath);
            var err = result.Stderr.Trim();
            var last = err.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? $"exit code {result.ExitCode}";
            throw new InvalidOperationException("Export failed: " + last, new Exception(err));
        }
        progress?.Report(new ExportProgress(1, TimeSpan.Zero, "Checking loudness…"));
        var reading = await Loudness.MeasureFileAsync(o.OutputPath, ct);
        progress?.Report(new ExportProgress(1, TimeSpan.Zero));
        return new ExportResult(reading);
    }

    private static double? ParseProgress(string line, double total)
    {
        if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) && !line.StartsWith("out_time_ms=", StringComparison.Ordinal))
            return null;
        if (!long.TryParse(line.AsSpan(line.IndexOf('=') + 1), out var us) || us < 0)
            return null;
        return Math.Clamp(us / 1_000_000.0 / total, 0, 1);
    }

    /// <summary>
    /// First loudness pass: renders only the final audio mix (same graph as the export, no video) and
    /// returns loudnorm's measurement of it. Also used by the editor's "Measure loudness".
    /// </summary>
    public static async Task<LoudnessReading?> MeasureMixAsync(EditorProject project, ExportOptions o, IProgress<double>? progress,
        CancellationToken ct)
    {
        if (FFmpeg.FfmpegPath == null || !project.CanExport) return null;
        await PrepareAsync(project);
        using var cmd = Build(project, o, measureOnly: true);
        var total = Math.Max(0.01, cmd.Duration);
        var result = await FFmpeg.RunAsync(FFmpeg.FfmpegPath, cmd.Args, ct, line =>
        {
            if (ParseProgress(line, total) is { } f) progress?.Report(f);
        });
        return result.ExitCode == 0 ? Loudness.ParseLoudnorm(result.Stderr) : null;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>ffmpeg filter for the paused-preview "exact frame" of a clip (colour only; size is the preview's).</summary>
    public static string? PreviewFilter(VideoClip clip) => ColorFilter(clip);
}
