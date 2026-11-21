using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CamStudio.Desktop.Media;

/// <summary>
/// --stress: 1080p30 noisy content for N seconds through LiveStreamer to a local ffmpeg RTMP server,
/// optionally through a bandwidth-limited TCP proxy (--uplink Mbps) that simulates a slow upload.
/// Reports how far the stream is behind real time at the end, and the quality it settled on.
/// </summary>
static class Stress
{
    public static async Task<int> RunAsync(string outDir, int seconds, string encoder, double uplinkMbps = 0, string quality = "1080p")
    {
        // ffmpeg's listen mode takes one connection, and stepping down reconnects, so relaunch it in a loop.
        using var serverStop = new CancellationTokenSource();
        Process? current = null;
        var serverLoop = Task.Run(async () =>
        {
            var i = 0;
            while (!serverStop.IsCancellationRequested)
            {
                using var p = current = Process.Start(new ProcessStartInfo("ffmpeg",
                    $"-hide_banner -loglevel error -y -listen 1 -i rtmp://127.0.0.1:19352/live/stress -c copy \"{Path.Combine(outDir, $"stress-{i++}.flv")}\"")
                    { UseShellExecute = false, CreateNoWindow = true })!;
                using var reg = serverStop.Token.Register(() => { try { p.Kill(); } catch { } });
                await p.WaitForExitAsync();
            }
        });
        await Task.Delay(800);

        var port = 19352;
        using var proxyStop = new CancellationTokenSource();
        Task? proxy = null;
        if (uplinkMbps > 0)
        {
            port = 19353;
            proxy = ThrottledProxy(port, 19352, uplinkMbps, proxyStop.Token);
        }
        var url = $"rtmp://127.0.0.1:{port}/live/stress";

        var (fw, fh) = quality == "4K" ? (3840, 2160) : (1920, 1080);
        var streamer = new LiveStreamer("ffmpeg", encoder, url, fw, fh, 30, quality, Path.Combine(outDir, "stream.log"));
        var sw = Stopwatch.StartNew();
        streamer.Changed += () =>
        {
            if (streamer.State != LiveStreamer.StreamState.Live || streamer.Lag > 0.5)
                Console.WriteLine($"{sw.Elapsed.TotalSeconds,5:0.0}s {streamer.State} {streamer.Current.Name} lag={streamer.Lag:0.0}s sent={streamer.SentKbps:0}k {streamer.LastError}");
        };
        streamer.Start();

        var rnd = new Random(1);
        // Camera-like content (gradient + a noisy band), compressible like real video.
        var frames = Enumerable.Range(0, 8).Select(k =>
        {
            var f = new Nv12Frame(fw, fh);
            for (var y = 0; y < fh; y++) f.Data.AsSpan(y * fw, fw).Fill((byte)(16 + (y + k * 9) % 200));
            rnd.NextBytes(f.Data.AsSpan(0, fw * fh / 16));
            f.Data.AsSpan(fw * fh).Fill(128);
            return f;
        }).ToArray();
        long n = 0, samples = 0;
        double maxLagLast10 = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            var f = new Nv12Frame(fw, fh);
            Buffer.BlockCopy(frames[n % frames.Length].Data, 0, f.Data, 0, f.Data.Length);
            streamer.PushVideo(f);
            var due = (long)(sw.Elapsed.TotalSeconds * 48000) - samples;
            if (due > 0) { streamer.PushAudio(new byte[due * 2]); samples += due; }
            if (sw.Elapsed.TotalSeconds > seconds - 10) maxLagLast10 = Math.Max(maxLagLast10, streamer.Lag);
            n++;
            var next = TimeSpan.FromSeconds(n / 30.0) - sw.Elapsed;
            if (next > TimeSpan.Zero) await Task.Delay(next);
        }
        var finalTier = streamer.Current.Name;
        var notice = streamer.Notice;
        await streamer.StopAsync();
        proxyStop.Cancel();
        serverStop.Cancel();
        try { current?.Kill(); } catch { }
        try { await serverLoop.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        try { current?.Kill(); } catch { }

        Console.WriteLine($"uplink {(uplinkMbps > 0 ? uplinkMbps + " Mbps" : "unlimited")}, encoder {encoder}: final quality {finalTier}, " +
                          $"max lag in the last 10 s {maxLagLast10:0.0}s");
        if (notice != null) Console.WriteLine("notice: " + notice);
        var ok = maxLagLast10 < 3;
        Console.WriteLine(ok ? "PASS (stays near real time)" : "FAIL (falls behind)");
        return ok ? 0 : 1;
    }

    /// <summary>Forwards TCP connections from listenPort to targetPort, limiting client→server bytes to mbps.</summary>
    private static async Task ThrottledProxy(int listenPort, int targetPort, double mbps, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, listenPort);
        listener.Start();
        ct.Register(() => listener.Stop());
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(async () =>
                {
                    using var c = client;
                    using var upstream = new TcpClient();
                    try
                    {
                        await upstream.ConnectAsync(IPAddress.Loopback, targetPort, ct);
                        // Small socket buffers so the limit pushes back on the sender like a real slow link.
                        c.ReceiveBufferSize = 16 * 1024;
                        var a = c.GetStream();
                        var b = upstream.GetStream();
                        var down = b.CopyToAsync(a, ct);
                        var bytesPerSec = mbps * 1_000_000 / 8;
                        var buf = new byte[4096];
                        var clock = Stopwatch.StartNew();
                        long sent = 0;
                        while (!ct.IsCancellationRequested)
                        {
                            var r = await a.ReadAsync(buf, ct);
                            if (r == 0) break;
                            await b.WriteAsync(buf.AsMemory(0, r), ct);
                            sent += r;
                            var ahead = sent / bytesPerSec - clock.Elapsed.TotalSeconds;
                            if (ahead > 0) await Task.Delay(TimeSpan.FromSeconds(ahead), ct);
                        }
                    }
                    catch { }
                }, ct);
            }
        }
        catch { }
    }
}
