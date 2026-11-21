// Screen share checks: compositor correctness (synthetic images), speed, real screen capture, and an
// end-to-end recording that turns sharing on and off. Nothing captured from the real screen is kept.
// Usage: dotnet run --project windows/tests/ShareSmoke -c Release -p:Platform=x64 -- [outdir]
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CamStudio.Desktop.Media;
using CamStudio.Desktop.Platform;

var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "camstudio-share-smoke"));
Directory.CreateDirectory(outDir);
var fails = 0;
void Check(bool ok, string what) { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) fails++; }

// ---------------------------------------------------------------- synthetic inputs
static ScreenCapture.Frame Screen(int w, int h)
{
    // A "desktop": light background, dark text-like stripes, a red box at the top right.
    var bgra = new byte[w * h * 4];
    for (var y = 0; y < h; y++)
    for (var x = 0; x < w; x++)
    {
        var i = (y * w + x) * 4;
        byte v = (byte)((y / 12) % 6 == 0 ? 40 : 235);
        bgra[i] = v; bgra[i + 1] = v; bgra[i + 2] = v; bgra[i + 3] = 255;
        if (x > w * 3 / 4 && y < h / 4) { bgra[i] = 30; bgra[i + 1] = 30; bgra[i + 2] = 220; }
    }
    return new ScreenCapture.Frame { Bgra = bgra, Width = w, Height = h, Ticks = Environment.TickCount64 };
}
static Nv12Frame Camera(int w, int h, byte y = 120)
{
    var f = new Nv12Frame(w, h);
    f.Data.AsSpan(0, w * h).Fill(y);
    for (var i = w * h; i < f.Data.Length; i += 2) { f.Data[i] = 90; f.Data[i + 1] = 170; } // skin-ish tone
    return f;
}

// ---------------------------------------------------------------- 1. geometry & colours
Console.WriteLine("1. Composition");
var dst = new Nv12Frame(1920, 1080);
SceneCompositor.Compose(Screen(2560, 1440), Camera(1920, 1080), dst, SceneCompositor.BubbleSize.Medium);
byte Y(Nv12Frame f, int x, int y) => f.Data[y * f.Width + x];
// Medium bubble: diameter 0.32 × 1080 = 345.6, margin 48.6 → centre (1698.6, 858.6) at the bottom right, outer radius 172.8.
Check(Math.Abs(Y(dst, 1698, 858) - 120) <= 2, $"bubble centre shows the camera (Y={Y(dst, 1698, 858)}, expected 120)");
Check(Y(dst, 1698, 858 - 170) >= 225, $"white ring at the bubble edge (Y={Y(dst, 1698, 858 - 170)})");
Check(Y(dst, 1700, 100) < 140 && dst.Data[1920 * 1080 + 50 * 1920 + 1700 + 1] > 180, "screen's red box appears at the top right");
Check(Y(dst, 1000, 500) is 235 or (> 40 and < 236), "screen fills the rest of the frame");
var portraitScreen = new Nv12Frame(1920, 1080);
SceneCompositor.Compose(Screen(1440, 1080), null, portraitScreen, SceneCompositor.BubbleSize.Medium);
Check(Y(portraitScreen, 50, 540) == 20 && Y(portraitScreen, 1870, 540) == 20, "4:3 screen is letterboxed (dark side bars)");
Check(Math.Abs(Y(portraitScreen, 1698, 858) - 120) > 20, "no camera → no bubble");
Check(Math.Abs(Y(dst, 221, 858) - 120) > 20, "nothing drawn at the bottom left any more");
var small = new Nv12Frame(1920, 1080);
SceneCompositor.Compose(Screen(1920, 1080), Camera(1280, 720, 60), small, SceneCompositor.BubbleSize.Small);
var large = new Nv12Frame(1920, 1080);
SceneCompositor.Compose(Screen(1920, 1080), Camera(1280, 720, 60), large, SceneCompositor.BubbleSize.Large);
Check(Math.Abs(Y(large, 1620, 760) - 60) <= 3 && Math.Abs(Y(small, 1620, 760) - 60) > 20, "bubble sizes differ (large covers more)");
SavePng(dst, Path.Combine(outDir, "composite-1080p.png"));
SavePng(large, Path.Combine(outDir, "composite-large.png"));
Console.WriteLine($"  saved {Path.Combine(outDir, "composite-1080p.png")}");

// ---------------------------------------------------------------- 2. speed
Console.WriteLine("2. Speed (median of 30 frames)");
foreach (var (w, h, sw, sh) in new[] { (1920, 1080, 2560, 1440), (1920, 1080, 1920, 1080), (3840, 2160, 3840, 2160) })
{
    var scr = Screen(sw, sh);
    var cam = Camera(w, h);
    var times = new List<double>();
    for (var i = 0; i < 30; i++)
    {
        var f = new Nv12Frame(w, h);
        var t0 = Stopwatch.GetTimestamp();
        SceneCompositor.Compose(scr, cam, f, SceneCompositor.BubbleSize.Medium);
        times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
    }
    times.Sort();
    var median = times[times.Count / 2];
    Console.WriteLine($"  {sw}x{sh} screen → {w}x{h}: {median:0.0} ms");
    Check(median < 1000.0 / 30 * 0.6, $"fits comfortably in a 30 fps frame ({median:0.0} ms < 20 ms)");
}

// ---------------------------------------------------------------- 3. real screen capture
Console.WriteLine("3. Real capture of the main screen (3 s, nothing saved)");
Check(ScreenCapture.IsSupported, "Windows Graphics Capture is supported");
using (var capture = ScreenCapture.ForPrimaryMonitor())
{
    var sw = Stopwatch.StartNew();
    var seen = new HashSet<long>();
    while (sw.Elapsed.TotalSeconds < 3)
    {
        if (capture.Latest is { } fr) seen.Add(fr.Ticks);
        await Task.Delay(5);
    }
    var last = capture.Latest;
    Console.WriteLine($"  source “{capture.Name}”, {last?.Width}x{last?.Height}, {seen.Count} frames in 3 s (screen only updates when something changes)");
    Check(last != null && last.Width > 0, "frames arrive");
    var nonBlack = last != null && last.Bgra.Where((b, i) => i % 4 != 3).Take(400_000).Any(b => b > 30);
    Check(nonBlack, "captured image has content");
}

// ---------------------------------------------------------------- 4. end to end: recording with share on/off
Console.WriteLine("4. Recording while switching screen share off → on → off");
{
    using var scene = new SceneCompositor { Fps = 30, CameraEnabled = true };
    var sizes = new HashSet<string>();
    var recorder = new VideoRecorder(1280, 720, 30, outDir);
    await recorder.Ready;
    var outputs = 0;
    scene.Output = f => { sizes.Add($"{f.Width}x{f.Height}"); recorder.PushVideo(f); Interlocked.Increment(ref outputs); };
    var sw = Stopwatch.StartNew();
    var n = 0;
    while (sw.Elapsed.TotalSeconds < 6)
    {
        var t = sw.Elapsed.TotalSeconds;
        if (t >= 2 && !scene.Sharing && t < 4) scene.SetScreen(ScreenCapture.ForPrimaryMonitor());
        if (t >= 4 && scene.Sharing) scene.SetScreen(null);
        scene.CameraFrame(Camera(1280, 720, (byte)(60 + n % 100)));
        n++;
        var next = TimeSpan.FromSeconds(n / 30.0) - sw.Elapsed;
        if (next > TimeSpan.Zero) await Task.Delay(next);
    }
    recorder.Finish();
    await recorder.Completion;
    var probe = Process.Start(new ProcessStartInfo("ffprobe", $"-v error -select_streams v:0 -show_entries stream=width,height:format=duration -of csv=p=0 \"{recorder.FilePath}\"")
        { RedirectStandardOutput = true, UseShellExecute = false })!;
    var info = (await probe.StandardOutput.ReadToEndAsync()).Replace("\r", "").Trim().Replace("\n", " ");
    Console.WriteLine($"  {outputs} frames out, sizes {string.Join(",", sizes)}, file: {info}");
    Check(sizes.Count == 1 && sizes.Contains("1280x720"), "video size never changes when toggling");
    Check(outputs >= 160, "about 30 frames per second throughout (≥ 160 in 6 s)");
    Check(info.StartsWith("1280,720"), "recording is 1280×720");
    File.Delete(recorder.FilePath); // it contains the real screen: don't keep it
}

Console.WriteLine(fails == 0 ? "PASS" : $"FAIL ({fails})");
return fails == 0 ? 0 : 1;

static void SavePng(Nv12Frame f, string path)
{
    var bgra = new byte[f.Width * f.Height * 4];
    Nv12Scaler.ToBgra(f, bgra, f.Width, f.Height, false);
    var bmp = BitmapSource.Create(f.Width, f.Height, 96, 96, PixelFormats.Bgra32, null, bgra, f.Width * 4);
    using var s = File.Create(path);
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    enc.Save(s);
}
