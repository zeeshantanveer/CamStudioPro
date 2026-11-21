// Publishes a synthetic phone feed through LiveStreamer to a local RTMP server (ffmpeg -listen 1)
// and checks the received stream. Usage: dotnet run --project windows/tests/LiveStreamSmoke -c Release -p:Platform=x64 -- <outdir>
using System.Diagnostics;
using CamStudio.Desktop.Media;

var outDir = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : Path.Combine(Path.GetTempPath(), "camstudio-live-smoke");
Directory.CreateDirectory(outDir);
if (args.Contains("--sync"))
    return await Sync.RunAsync(outDir);
if (args.Contains("--trace"))
    return await Trace.RunAsync(outDir, args.SkipWhile(a => a != "--trace").Skip(1).FirstOrDefault() ?? "variable");
if (args.Contains("--policy"))
    return PolicySim.RunAll();
if (args.Contains("--judder"))
    return await Judder.RunAsync(outDir, args.SkipWhile(a => a != "--encoder").Skip(1).FirstOrDefault() ?? "libx264");
if (args.Contains("--stress"))
{
    string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
    var up = double.TryParse(Arg("--uplink"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var u) ? u : 0;
    return await Stress.RunAsync(outDir, int.TryParse(Arg("--seconds"), out var sec) ? sec : 30, Arg("--encoder") ?? "libx264", up, Arg("--quality") ?? "1080p");
}
var flv = Path.Combine(outDir, "received.flv");
File.Delete(flv);
const string url = "rtmp://127.0.0.1:19350/live/test";

var server = Process.Start(new ProcessStartInfo("ffmpeg",
    $"-hide_banner -loglevel error -y -listen 1 -i {url} -c copy \"{flv}\"") { UseShellExecute = false, CreateNoWindow = true })!;
await Task.Delay(800);

var streamer = new LiveStreamer("ffmpeg", "libx264", url, 1280, 720, 30, "720p");
var states = new List<LiveStreamer.StreamState>();
streamer.Changed += () => { lock (states) states.Add(streamer.State); Console.WriteLine($"state: {streamer.State} {streamer.LastError}"); };
streamer.Start();

// 3 s of 640x480 frames + 440 Hz tone, then a 2 s "phone stall", then 2 s more video.
var sw = Stopwatch.StartNew();
var frame = new Nv12Frame(640, 480);
long samples = 0;
while (sw.Elapsed < TimeSpan.FromSeconds(7))
{
    var t = sw.Elapsed.TotalSeconds;
    var stalled = t is > 3 and < 5;
    if (!stalled)
    {
        var f = new Nv12Frame(640, 480);
        var shade = (byte)(16 + (int)(t * 40) % 200);
        f.Data.AsSpan(0, 640 * 480).Fill(shade);
        f.Data.AsSpan(640 * 480).Fill(128);
        streamer.PushVideo(f);
        var due = (long)(t * 48000) - samples;
        var pcm = new byte[due * 2];
        for (var i = 0; i < due; i++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * 440 * (samples + i) / 48000) * 8000);
            pcm[2 * i] = (byte)v; pcm[2 * i + 1] = (byte)(v >> 8);
        }
        samples += due;
        streamer.PushAudio(pcm);
    }
    else samples = (long)(t * 48000);
    await Task.Delay(33);
}
await streamer.StopAsync();
await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

var probe = Process.Start(new ProcessStartInfo("ffprobe",
    $"-v error -show_entries format=duration:stream=codec_type,codec_name,width,height,sample_rate,channels -of compact \"{flv}\"")
    { RedirectStandardOutput = true, UseShellExecute = false })!;
var info = await probe.StandardOutput.ReadToEndAsync();
await probe.WaitForExitAsync();
Console.WriteLine(info.Trim());

var ok = states.Contains(LiveStreamer.StreamState.Live)
         && info.Contains("codec_name=h264") && info.Contains("width=1280") && info.Contains("height=720")
         && info.Contains("codec_name=aac") && info.Contains("sample_rate=48000");
var dur = info.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("format|duration="))?.Split('=')[1];
Console.WriteLine($"duration: {dur}");
ok &= double.TryParse(dur, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 5.5;
Console.WriteLine(ok ? "PASS" : "FAIL");
return ok ? 0 : 1;
