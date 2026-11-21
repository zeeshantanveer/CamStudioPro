using System.Diagnostics;
using CamStudio.Desktop.Media;

/// <summary>
/// --sync: lip-sync check through a slow-connecting server (5 s handshake, like YouTube). Once a second a
/// white frame and a 1 kHz beep are sent at the same moment; the received file must keep them together.
/// </summary>
static class Sync
{
    public static async Task<int> RunAsync(string outDir)
    {
        var flv = Path.Combine(outDir, "sync.flv");
        File.Delete(flv);
        using var stop = new CancellationTokenSource();
        var server = Process.Start(new ProcessStartInfo("ffmpeg",
            $"-hide_banner -loglevel error -y -listen 1 -i rtmp://127.0.0.1:19372/live/s -c copy \"{flv}\"") { UseShellExecute = false, CreateNoWindow = true })!;
        await Task.Delay(800);
        _ = Trace.ScheduledProxy(19373, 19372, () => 20, TimeSpan.FromSeconds(5), stop.Token);

        var streamer = new LiveStreamer("ffmpeg", "libx264", "rtmp://127.0.0.1:19373/live/s", 1280, 720, 30, "720p",
            Path.Combine(outDir, "sync.log"));
        streamer.Start();
        var sw = Stopwatch.StartNew();
        long frame = 0, samples = 0;
        while (sw.Elapsed.TotalSeconds < 20)
        {
            var t = sw.Elapsed.TotalSeconds;
            var flash = frame % 30 == 0;
            var f = new Nv12Frame(1280, 720);
            f.Data.AsSpan(0, 1280 * 720).Fill(flash ? (byte)235 : (byte)30);
            f.Data.AsSpan(1280 * 720).Fill(128);
            streamer.PushVideo(f);
            var due = (long)(t * 48000) - samples;
            if (due > 0)
            {
                var pcm = new byte[due * 2];
                for (var i = 0; i < due; i++)
                {
                    // Beep for the first 100 ms of every second of capture time.
                    var st = (samples + i) / 48000.0;
                    short v = st % 1.0 < 0.1 ? (short)(Math.Sin(2 * Math.PI * 1000 * st) * 12000) : (short)0;
                    pcm[2 * i] = (byte)v; pcm[2 * i + 1] = (byte)(v >> 8);
                }
                samples += due;
                streamer.PushAudio(pcm);
            }
            frame++;
            var next = TimeSpan.FromSeconds(frame / 30.0) - sw.Elapsed;
            if (next > TimeSpan.Zero) await Task.Delay(next);
        }
        await streamer.StopAsync();
        await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        stop.Cancel();

        // Flash times from video frame timestamps; beep onsets from audio samples.
        var probe = Process.Start(new ProcessStartInfo("ffprobe",
            $"-v error -select_streams v:0 -show_entries frame=pts_time -of csv=p=0 \"{flv}\"") { RedirectStandardOutput = true, UseShellExecute = false })!;
        var pts = (await probe.StandardOutput.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => double.Parse(l.Trim().TrimEnd(','), System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var vdec = Process.Start(new ProcessStartInfo("ffmpeg", $"-v error -i \"{flv}\" -fps_mode passthrough -vf scale=2:2 -pix_fmt yuv420p -f rawvideo -")
            { RedirectStandardOutput = true, UseShellExecute = false })!;
        using var vms = new MemoryStream();
        await vdec.StandardOutput.BaseStream.CopyToAsync(vms);
        var vraw = vms.ToArray();
        var flashes = new List<double>();
        for (var i = 0; i < Math.Min(pts.Count, vraw.Length / 6); i++)
            if (vraw[i * 6] > 150 && (i == 0 || vraw[(i - 1) * 6] <= 150)) flashes.Add(pts[i]);
        var dec = Process.Start(new ProcessStartInfo("ffmpeg", $"-v error -i \"{flv}\" -vn -ac 1 -ar 8000 -f s16le -") { RedirectStandardOutput = true, UseShellExecute = false })!;
        using var ms = new MemoryStream();
        await dec.StandardOutput.BaseStream.CopyToAsync(ms);
        var a = ms.ToArray();
        var audioStart = await StartTime(flv, "a");
        var onsets = new List<double>();
        var quiet = 0;
        for (var i = 0; i + 1 < a.Length; i += 2)
        {
            var v = Math.Abs((short)(a[i] | a[i + 1] << 8));
            if (v > 3000 && quiet > 400) onsets.Add(audioStart + i / 2 / 8000.0);
            quiet = v > 3000 ? 0 : quiet + 1;
        }
        var offsets = flashes.Select(fl => onsets.Count == 0 ? double.NaN : onsets.MinBy(o => Math.Abs(o - fl)) - fl)
            .Where(d => Math.Abs(d) < 0.5).ToList();
        Console.WriteLine($"flashes {flashes.Count}, beeps {onsets.Count}, A−V offsets (ms): {string.Join(" ", offsets.Select(o => $"{o * 1000:0}"))}");
        // The first second after connecting may settle (≤ 185 ms late audio: ITU-R BT.1359 acceptability; its
        // detectability threshold is 125 ms). After that, lip sync must stay within one frame (40 ms).
        var ok = offsets.Count >= 8 && offsets.Take(2).All(o => o <= 0.185 && o >= -0.090) && offsets.Skip(2).All(o => Math.Abs(o) <= 0.040);
        Console.WriteLine(ok ? "PASS (settles within 1 s, then within one frame)" : "FAIL");
        return ok ? 0 : 1;
    }

    private static async Task<double> StartTime(string file, string stream)
    {
        var p = Process.Start(new ProcessStartInfo("ffprobe", $"-v error -select_streams {stream}:0 -show_entries stream=start_time -of csv=p=0 \"{file}\"")
            { RedirectStandardOutput = true, UseShellExecute = false })!;
        var s = (await p.StandardOutput.ReadToEndAsync()).Trim();
        return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
