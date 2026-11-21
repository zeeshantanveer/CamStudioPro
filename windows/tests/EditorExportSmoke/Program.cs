// Headless check of the editor's export pipeline: generates test media with ffmpeg, builds a project
// (trim, 2x speed, fades, colour, image clip, title, music) and verifies the exported MP4 with ffprobe.
// Usage: dotnet run --project windows/tests/EditorExportSmoke -c Release -p:Platform=x64 -- <work dir>
using System.Globalization;
using System.IO;
using System.Text.Json;
using CamStudio.Editor.Engine;
using CamStudio.Editor.Model;

var work = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "camstudio-editor-smoke");
Directory.CreateDirectory(work);
if (!FFmpeg.Locate()) { Console.WriteLine("FAIL: ffmpeg/ffprobe not found"); return 1; }
Console.WriteLine($"ffmpeg: {FFmpeg.FfmpegPath}");

async Task Gen(string file, params string[] a)
{
    var r = await FFmpeg.RunAsync(FFmpeg.FfmpegPath!, new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(a).Append(file));
    if (r.ExitCode != 0) throw new Exception($"generate {file}: {r.Stderr}");
}
var clipA = Path.Combine(work, "a.mp4");
var clipB = Path.Combine(work, "b.mp4");
var music = Path.Combine(work, "music.m4a");
var image = Path.Combine(work, "card.png");
await Gen(clipA, "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=4", "-f", "lavfi", "-i", "sine=f=440:d=4", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest");
await Gen(clipB, "-f", "lavfi", "-i", "testsrc2=s=640x480:r=25:d=3", "-f", "lavfi", "-i", "sine=f=660:d=3", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest");
await Gen(music, "-f", "lavfi", "-i", "sine=f=220:d=6", "-c:a", "aac");
await Gen(image, "-f", "lavfi", "-i", "color=c=0x3366cc:s=800x600", "-frames:v", "1");

var project = new EditorProject { Name = "smoke", Width = 1280, Height = 720, Fps = 30 };
foreach (var f in new[] { clipA, clipB, music, image })
{
    var asset = await FFmpeg.ProbeAsync(f) ?? throw new Exception("probe failed: " + f);
    project.Assets.Add(asset);
    Console.WriteLine($"probe {Path.GetFileName(f)}: {asset.Duration:0.00}s {asset.Width}x{asset.Height} video={asset.HasVideo} audio={asset.HasAudio} image={asset.IsImage}");
}

var a = project.CreateVideoClip(project.Assets[0]);
a.In = 0.5; a.Out = 3.5;                     // 3.0 s
a.FadeIn = 0.5; a.Brightness = 0.05; a.Saturation = 1.3;
var b = project.CreateVideoClip(project.Assets[1]);
b.Speed = 2;                                 // 3 s source -> 1.5 s
b.FadeOut = 0.5;
var card = project.CreateVideoClip(project.Assets[3]);
card.Out = 1.0;                              // 1.0 s still
project.Video.AddRange(new[] { a, b, card });
project.Titles.Add(new TitleClip { Text = "Hello, world: it's \"CamStudio\" 100%", Start = 0.5, Duration = 2, Position = TitlePosition.LowerThird });
project.Titles.Add(new TitleClip { Text = "Second line\nwith a break", Start = 3.2, Duration = 1.5, Position = TitlePosition.Center, Color = TitleColor.Accent, Box = false });
project.Music.Add(project.CreateMusicClip(project.Assets[2], 1.0));
project.Layout();
var expected = project.Duration;             // 5.5 s

// project file round trip
var reloaded = EditorProject.FromJson(project.ToJson());
if (Math.Abs(reloaded.Duration - expected) > 1e-6) { Console.WriteLine("FAIL: project JSON round trip changed duration"); return 1; }

var output = Path.Combine(work, "export-720p.mp4");
var encoder = await FFmpeg.ResolveEncoderAsync(hevc: false) ?? throw new Exception("no H.264 encoder");
Console.WriteLine($"encoder: {encoder}");
var sw = System.Diagnostics.Stopwatch.StartNew();
double lastFraction = 0;
await ExportBuilder.ExportAsync(project, new ExportOptions(1280, 720, 30, ExportQuality.Standard, encoder, output),
    new Progress<ExportProgress>(p => lastFraction = p.Fraction), CancellationToken.None);
Console.WriteLine($"exported in {sw.Elapsed.TotalSeconds:0.0}s -> {output}");

var probe = await FFmpeg.RunAsync(FFmpeg.FfprobePath!, new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", output });
using var doc = JsonDocument.Parse(probe.Stdout);
var root = doc.RootElement;
var duration = double.Parse(root.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
var video = root.GetProperty("streams").EnumerateArray().First(s => s.GetProperty("codec_type").GetString() == "video");
var hasAudio = root.GetProperty("streams").EnumerateArray().Any(s => s.GetProperty("codec_type").GetString() == "audio");
var w = video.GetProperty("width").GetInt32();
var h = video.GetProperty("height").GetInt32();
Console.WriteLine($"output: {duration:0.000}s (expected {expected:0.000}s), {w}x{h}, audio={hasAudio}, codec={video.GetProperty("codec_name").GetString()}");

var failures = new List<string>();
if (Math.Abs(duration - expected) > 0.2) failures.Add($"duration {duration:0.000} != {expected:0.000}");
if (w != 1280 || h != 720) failures.Add($"size {w}x{h}");
if (!hasAudio) failures.Add("no audio stream");

// cancel path: start a long export and cancel it; the partial file must be removed
var cancelOut = Path.Combine(work, "export-cancel.mp4");
using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400)))
{
    var big = EditorProject.FromJson(project.ToJson());
    for (var i = 0; i < 40; i++) big.Video.Add(big.CreateVideoClip(big.Assets[0]));
    big.Layout();
    try
    {
        await ExportBuilder.ExportAsync(big, new ExportOptions(1920, 1080, 30, ExportQuality.High, encoder, cancelOut), null, cts.Token);
        failures.Add("cancel: export finished before cancel");
    }
    catch (OperationCanceledException) { }
}
if (File.Exists(cancelOut)) failures.Add("cancel: partial file left behind");
else Console.WriteLine("cancel: partial file removed");

// exact-frame preview with the colour filter
var frame = await FFmpeg.FrameAsync(clipA, 1.0, 320, ExportBuilder.PreviewFilter(a));
if (frame == null) failures.Add("preview frame failed");
else Console.WriteLine($"preview frame: {frame.PixelWidth}x{frame.PixelHeight}");

// ================================================================ audio editing
async Task<double> MeanVolume(string file, double from, double length)
{
    var r = await FFmpeg.RunAsync(FFmpeg.FfmpegPath!, new[]
        { "-hide_banner", "-ss", from.ToString(CultureInfo.InvariantCulture), "-t", length.ToString(CultureInfo.InvariantCulture),
          "-i", file, "-af", "volumedetect", "-f", "null", "-" });
    var line = r.Stderr.Split('\n').FirstOrDefault(l => l.Contains("mean_volume:"));
    if (line == null) return double.NaN;
    var v = line[(line.IndexOf("mean_volume:") + 12)..].Replace("dB", "").Trim();
    return double.Parse(v, CultureInfo.InvariantCulture);
}
async Task<double> Duration(string file)
{
    var r = await FFmpeg.RunAsync(FFmpeg.FfprobePath!, new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file });
    return double.Parse(r.StdoutText.Trim(), CultureInfo.InvariantCulture);
}
async Task ExportAudio(EditorProject p, string file, AudioFormat format) =>
    await ExportBuilder.ExportAsync(p, new ExportOptions(0, 0, 30, ExportQuality.Standard, "", file, format), null, CancellationToken.None);

// Noisy speech stand-in: 1 s of pure white noise, then a 440 Hz tone over the same noise (4 s total), plus 50 Hz hum.
var noisy = Path.Combine(work, "noisy.wav");
await Gen(noisy, "-f", "lavfi", "-i", "anoisesrc=d=4:c=white:a=0.06:r=48000",
    "-f", "lavfi", "-i", "sine=f=440:d=3:sample_rate=48000",
    "-f", "lavfi", "-i", "sine=f=50:d=4:sample_rate=48000",
    "-filter_complex", "[1]volume=0.5,adelay=1000:all=1[t];[2]volume=0.05[h];[0][t][h]amix=inputs=3:normalize=0:duration=first",
    "-c:a", "pcm_s16le");
var noisyAsset = await FFmpeg.ProbeAsync(noisy) ?? throw new Exception("probe noisy");

EditorProject AudioProject(Action<MusicClip>? setup)
{
    var p = new EditorProject { Name = "audio", LoudnessTarget = LoudnessTarget.Off };
    p.Assets.Add(noisyAsset);
    var m = p.CreateMusicClip(noisyAsset, 0);
    m.Volume = 1; m.FadeIn = 0; m.FadeOut = 0;
    setup?.Invoke(m);
    p.Music.Add(m);
    return p;
}

var raw = Path.Combine(work, "noisy-raw.m4a");
var clean = Path.Combine(work, "noisy-clean.m4a");
var cleanNorm = Path.Combine(work, "noisy-clean-norm.m4a");
await ExportAudio(AudioProject(null), raw, AudioFormat.Aac);
await ExportAudio(AudioProject(m => { m.NoiseReduction = NoiseLevel.Strong; m.RemoveHum = true; }), clean, AudioFormat.Aac);
await ExportAudio(AudioProject(m => { m.NoiseReduction = NoiseLevel.Strong; m.RemoveHum = true; m.Normalize = true; }), cleanNorm, AudioFormat.Aac);

foreach (var f in new[] { raw, clean, cleanNorm })
{
    var d = await Duration(f);
    if (Math.Abs(d - 4) > 0.2) failures.Add($"{Path.GetFileName(f)} duration {d:0.000} != 4");
}
double rawNoise = await MeanVolume(raw, 0.3, 0.6), rawTone = await MeanVolume(raw, 2, 1.5);
double cleanNoise = await MeanVolume(clean, 0.3, 0.6), cleanTone = await MeanVolume(clean, 2, 1.5);
double normNoise = await MeanVolume(cleanNorm, 0.3, 0.6), normTone = await MeanVolume(cleanNorm, 2, 1.5);
Console.WriteLine($"noise-only RMS: raw {rawNoise:0.0} dB · cleaned {cleanNoise:0.0} dB ({rawNoise - cleanNoise:0.0} dB less)");
Console.WriteLine($"tone RMS:       raw {rawTone:0.0} dB · cleaned {cleanTone:0.0} dB");
Console.WriteLine($"SNR: raw {rawTone - rawNoise:0.0} dB · cleaned {cleanTone - cleanNoise:0.0} dB · cleaned+normalized {normTone - normNoise:0.0} dB (normalized tone {normTone:0.0} dB)");
if (rawNoise - cleanNoise < 6) failures.Add($"noise reduction only {rawNoise - cleanNoise:0.0} dB");
if (Math.Abs(rawTone - cleanTone) > 3) failures.Add($"tone changed by {cleanTone - rawTone:0.0} dB");
if ((normTone - normNoise) - (rawTone - rawNoise) < 6) failures.Add("normalized export lost the SNR improvement");

// MP3 / WAV audio-only formats
var encoders = await FFmpeg.EncodersAsync();
if (encoders.Contains("libmp3lame"))
{
    var mp3 = Path.Combine(work, "noisy.mp3");
    await ExportAudio(AudioProject(null), mp3, AudioFormat.Mp3);
    var d = await Duration(mp3);
    Console.WriteLine($"mp3: {d:0.000}s");
    if (Math.Abs(d - 4) > 0.2) failures.Add($"mp3 duration {d:0.000}");
}
else Console.WriteLine("mp3: libmp3lame not in this build (format hidden in the UI)");

// Video project whose clip audio is cleaned, exported as MP4 and as audio only
var noisyVideo = Path.Combine(work, "noisy-video.mp4");
await Gen(noisyVideo, "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=4", "-i", noisy, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest");
var nvAsset = await FFmpeg.ProbeAsync(noisyVideo) ?? throw new Exception("probe noisy video");
var vp = new EditorProject { Name = "vclean", Width = 640, Height = 360, Fps = 30, LoudnessTarget = LoudnessTarget.Off };
vp.Assets.Add(nvAsset);
var vclip = vp.CreateVideoClip(nvAsset);
vclip.NoiseReduction = NoiseLevel.Medium; vclip.RemoveHum = true;
vp.Video.Add(vclip);
vp.Layout();
var vOut = Path.Combine(work, "video-clean.mp4");
await ExportBuilder.ExportAsync(vp, new ExportOptions(640, 360, 30, ExportQuality.Small, encoder, vOut), null, CancellationToken.None);
var vDur = await Duration(vOut);
var vNoise = await MeanVolume(vOut, 0.3, 0.6);
Console.WriteLine($"video with cleaned audio: {vDur:0.000}s, noise-only {vNoise:0.0} dB (raw {rawNoise:0.0} dB)");
if (Math.Abs(vDur - 4) > 0.25) failures.Add($"video-clean duration {vDur:0.000}");
if (rawNoise - vNoise < 4) failures.Add("video clip noise reduction had no effect");

// Remove the audio part (Delete on A1 = Muted): the exported sound of that clip is silent
var mutedProject = EditorProject.FromJson(vp.ToJson());
mutedProject.Video[0].Muted = true;
var silentOut = Path.Combine(work, "video-audio-removed.wav");
await ExportAudio(mutedProject, silentOut, AudioFormat.Wav);
var silentLevel = await MeanVolume(silentOut, 0.2, 3.5);
Console.WriteLine($"audio removed: {await Duration(silentOut):0.000}s, level {silentLevel:0.0} dB");
if (!(silentLevel < -70)) failures.Add($"removed audio is not silent ({silentLevel:0.0} dB)");

// Split a music clip at 1.5 s
var sp = AudioProject(m => m.NoiseReduction = NoiseLevel.Light);
var right = sp.SplitAt(sp.Music[0], 1.5) as MusicClip;
if (right == null || sp.Music.Count != 2) failures.Add("music split failed");
else
{
    var left = sp.Music[0];
    Console.WriteLine($"music split: left {left.Start:0.00}+{left.Duration:0.00}s, right {right.Start:0.00}+{right.Duration:0.00}s (In {right.In:0.00}), right keeps NR={right.NoiseReduction}");
    if (Math.Abs(left.Duration - 1.5) > 1e-6 || Math.Abs(right.Start - 1.5) > 1e-6 || Math.Abs(right.In - 1.5) > 1e-6 || right.NoiseReduction != NoiseLevel.Light)
        failures.Add("music split geometry");
    sp.Music.Remove(left); // delete the first half: export must still be 4 s long? no: audio-only length = music end
    var splitOut = Path.Combine(work, "music-split.m4a");
    await ExportAudio(sp, splitOut, AudioFormat.Aac);
    var d = await Duration(splitOut);
    var lead = await MeanVolume(splitOut, 0.2, 1.0);
    Console.WriteLine($"after deleting the left half: {d:0.000}s, first second {lead:0.0} dB (silence)");
    if (Math.Abs(d - 4) > 0.2) failures.Add($"split export duration {d:0.000}");
    if (!(lead < -70)) failures.Add("deleted half not silent");
}

// JSON round trip of the new audio fields
var rt = EditorProject.FromJson(AudioProject(m => { m.NoiseReduction = NoiseLevel.Medium; m.RemoveHum = true; m.Normalize = true; m.Muted = true; }).ToJson());
var rtm = rt.Music[0];
var vrt = EditorProject.FromJson(vp.ToJson()).Video[0];
if (rtm.NoiseReduction != NoiseLevel.Medium || !rtm.RemoveHum || !rtm.Normalize || !rtm.Muted || vrt.NoiseReduction != NoiseLevel.Medium || !vrt.RemoveHum)
    failures.Add("JSON round trip of audio fields");
else Console.WriteLine("json: audio fields round-trip");
// Old project files (no audio fields) still load with clean-up off
var legacy = EditorProject.FromJson("{\"Name\":\"old\",\"Music\":[{\"Path\":\"x.mp3\",\"In\":0,\"Out\":2}],\"Video\":[]}");
if (legacy.Music[0].NoiseReduction != NoiseLevel.Off || legacy.Music[0].Muted) failures.Add("legacy project defaults");
else Console.WriteLine("json: legacy project loads with clean-up off");

// Cleaned-audio preview proxy: same length as the source, noise reduced
var proxyClip = AudioProject(m => { m.NoiseReduction = NoiseLevel.Strong; m.RemoveHum = true; }).Music[0];
var proxyReady = new TaskCompletionSource();
AudioProxy.Changed += () => proxyReady.TrySetResult();
var proxy = AudioProxy.Get(proxyClip);
if (proxy == null)
{
    await Task.WhenAny(proxyReady.Task, Task.Delay(60_000));
    proxy = AudioProxy.Get(proxyClip);
}
if (proxy == null) failures.Add("preview proxy not rendered");
else
{
    var pd = await Duration(proxy);
    var pn = await MeanVolume(proxy, 0.3, 0.6);
    Console.WriteLine($"preview proxy: {pd:0.000}s, noise-only {pn:0.0} dB");
    if (Math.Abs(pd - 4) > 0.15) failures.Add($"proxy duration {pd:0.000}");
    if (rawNoise - pn < 6) failures.Add("proxy not cleaned");
}

// Waveform peaks: 100 per second, louder where the tone is
var waveReady = new TaskCompletionSource();
Waveforms.Loaded += p => { if (string.Equals(p, noisy, StringComparison.OrdinalIgnoreCase)) waveReady.TrySetResult(); };
var peaks = Waveforms.Get(noisy);
if (peaks == null) { await Task.WhenAny(waveReady.Task, Task.Delay(30_000)); peaks = Waveforms.Get(noisy); }
if (peaks == null || peaks.Length < 390 || peaks.Length > 410) failures.Add($"waveform peaks {peaks?.Length}");
else
{
    var quiet = peaks.Skip(20).Take(60).Average();
    var loud = peaks.Skip(200).Take(150).Average();
    Console.WriteLine($"waveform: {peaks.Length} peaks, noise part {quiet:0.000}, tone part {loud:0.000}");
    if (loud <= quiet) failures.Add("waveform doesn't show the tone");
}

// ================================================================ voice enhancement (real speech)
failures.AddRange(await VoiceTests.RunAsync(work));

if (failures.Count > 0) { Console.WriteLine("FAIL: " + string.Join("; ", failures)); return 1; }
Console.WriteLine("PASS");
return 0;
