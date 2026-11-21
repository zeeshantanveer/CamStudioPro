using System.Diagnostics;
using CamStudio.Desktop.Media;

/// <summary>
/// --judder: frames arrive in bursts of two every 66 ms (like a real decoder). Each frame has its own
/// brightness, so decoding the received stream shows whether frames were skipped or repeated.
/// </summary>
static class Judder
{
    public static async Task<int> RunAsync(string outDir, string encoder = "libx264")
    {
        var flv = Path.Combine(outDir, "judder.flv");
        File.Delete(flv);
        const string url = "rtmp://127.0.0.1:19354/live/judder";
        var server = Process.Start(new ProcessStartInfo("ffmpeg",
            $"-hide_banner -loglevel error -y -listen 1 -i {url} -c copy \"{flv}\"") { UseShellExecute = false, CreateNoWindow = true })!;
        await Task.Delay(800);

        var log = Path.Combine(outDir, "judder.log");
        File.Delete(log);
        var streamer = new LiveStreamer("ffmpeg", encoder, url, 1280, 720, 30, "720p", log);
        var t0 = Stopwatch.StartNew();
        streamer.Changed += () => { if (streamer.State != LiveStreamer.StreamState.Live || streamer.Lag > 0.3) Console.WriteLine($"{t0.Elapsed.TotalSeconds:0.0}s {streamer.State} lag={streamer.Lag:0.00}"); };
        streamer.Start();
        static byte Level(long n) => (byte)(20 + n % 30 * 7);
        var sw = Stopwatch.StartNew();
        long n = 0, samples = 0;
        while (sw.Elapsed.TotalSeconds < 12)
        {
            for (var k = 0; k < 2; k++, n++)
            {
                var f = new Nv12Frame(1280, 720);
                f.Data.AsSpan(0, 1280 * 720).Fill(Level(n));
                f.Data.AsSpan(1280 * 720).Fill(128);
                streamer.PushVideo(f);
            }
            var due = (long)(sw.Elapsed.TotalSeconds * 48000) - samples;
            if (due > 0) { streamer.PushAudio(new byte[due * 2]); samples += due; }
            var next = TimeSpan.FromSeconds(n / 30.0) - sw.Elapsed;
            if (next > TimeSpan.Zero) await Task.Delay(next);
        }
        await streamer.StopAsync();
        await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

        // Decode to one gray pixel per frame and map brightness back to the frame's position in the 30-cycle.
        var dec = Process.Start(new ProcessStartInfo("ffmpeg",
            $"-v error -i \"{flv}\" -fps_mode passthrough -vf scale=2:2 -pix_fmt yuv420p -f rawvideo -") { RedirectStandardOutput = true, UseShellExecute = false })!;
        using var ms = new MemoryStream();
        await dec.StandardOutput.BaseStream.CopyToAsync(ms);
        await dec.WaitForExitAsync();
        var raw = ms.ToArray();
        // 2x2 yuv420p frames: 4 luma bytes then U, V. Use the first luma byte of each frame.
        var idx = Enumerable.Range(0, raw.Length / 6).Select(i => raw[i * 6])
            .Select(b => (int)Math.Round((b - 20) / 7.0)).Select(i => Math.Clamp(i, 0, 29)).ToArray();
        // Skip the first second (start-up) and look at steps between consecutive output frames.
        int ok = 0, repeat = 0, skip = 0;
        for (var i = 31; i < idx.Length - 30; i++)
        {
            var step = (idx[i] - idx[i - 1] + 30) % 30;
            if (step == 1) ok++; else if (step == 0) repeat++; else skip++;
        }
        var total = ok + repeat + skip;
        Console.WriteLine($"{encoder} frames {idx.Length}: in order {ok}, repeated {repeat}, skipped {skip} ({100.0 * (repeat + skip) / Math.Max(1, total):0.0}% judder)");
        var pass = total > 200 && (repeat + skip) <= total * 0.02;
        Console.WriteLine(pass ? "PASS" : "FAIL");
        return pass ? 0 : 1;
    }
}
