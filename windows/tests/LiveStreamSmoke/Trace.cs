using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CamStudio.Desktop.Media;

/// <summary>
/// --trace variable|outage: real ffmpeg publishing through a proxy whose bandwidth follows a schedule.
/// Upgrade timers are shortened (15 s instead of 60 s) so a run takes about two minutes.
///   variable: 8 Mbps → 1.5 Mbps at 30 s → 8 Mbps at 65 s. Expect: step down during the drop, climb back.
///   outage:   8 Mbps with a 4 s total outage at 40 s. Expect: no quality drop.
/// </summary>
static class Trace
{
    public static async Task<int> RunAsync(string outDir, string kind)
    {
        Func<double, double> mbps = kind switch
        {
            "outage" => t => t >= 40 && t < 44 ? 0 : 8,
            "connect" => _ => 8,
            "stall" => t => t >= 9 && t < 26 ? 0 : 8,
            _ => t => t < 30 ? 8 : t < 65 ? 1.5 : 8,
        };
        var seconds = kind switch { "outage" => 75, "connect" => 60, "stall" => 42, _ => 125 };
        // "connect": every new connection waits 5 s before data flows, like YouTube's RTMP handshake.
        var connectDelay = kind == "connect" ? TimeSpan.FromSeconds(5) : TimeSpan.Zero;

        using var stop = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                using var p = Process.Start(new ProcessStartInfo("ffmpeg",
                    $"-hide_banner -loglevel error -y -listen 1 -i rtmp://127.0.0.1:19362/live/t -c copy \"{Path.Combine(outDir, $"trace-{kind}-{i++}.flv")}\"")
                    { UseShellExecute = false, CreateNoWindow = true })!;
                using var reg = stop.Token.Register(() => { try { p.Kill(); } catch { } });
                await p.WaitForExitAsync();
            }
        });
        await Task.Delay(800);
        var sw = Stopwatch.StartNew();
        var proxy = ScheduledProxy(19363, 19362, () => mbps(sw.Elapsed.TotalSeconds), connectDelay, stop.Token);

        var csv = Path.Combine(outDir, $"trace-{kind}.csv");
        File.Delete(csv);
        var options = new StreamRatePolicy.Options
        {
            StableSecondsBeforeUpgrade = kind == "stall" ? 60 : 15,
            FailedUpgradeWindowSeconds = 12
        };
        var logPath = Path.Combine(outDir, $"trace-{kind}.log");
        File.Delete(logPath);
        var streamer = new LiveStreamer("ffmpeg", "libx264", "rtmp://127.0.0.1:19363/live/t", 1920, 1080, 30, "Auto",
            logPath, csv, options);
        var timeline = new List<(double T, string Step, double Lag, LiveStreamer.StreamState State)>();
        streamer.Changed += () =>
        {
            if (streamer.State == LiveStreamer.StreamState.Reconnecting && streamer.Notice != null)
                Console.WriteLine($"{sw.Elapsed.TotalSeconds,6:0.0}s {streamer.Notice}");
        };
        streamer.Start();

        // A realistic-ish moving picture: gradient shifting each frame (compressible like a camera).
        var frame = 0L;
        long samples = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            var f = new Nv12Frame(1920, 1080);
            for (var y = 0; y < 1080; y++)
                f.Data.AsSpan(y * 1920, 1920).Fill((byte)(16 + (y + frame * 3) % 200));
            new Random((int)frame).NextBytes(f.Data.AsSpan(0, 1920 * 60)); // a noisy band so the encoder has work
            f.Data.AsSpan(1920 * 1080).Fill(128);
            streamer.PushVideo(f);
            var due = (long)(sw.Elapsed.TotalSeconds * 48000) - samples;
            if (due > 0) { streamer.PushAudio(new byte[due * 2]); samples += due; }
            if (frame % 30 == 0) timeline.Add((sw.Elapsed.TotalSeconds, streamer.Current.Name, streamer.Lag, streamer.State));
            frame++;
            var next = TimeSpan.FromSeconds(frame / 30.0) - sw.Elapsed;
            if (next > TimeSpan.Zero) await Task.Delay(next);
        }
        await streamer.StopAsync();
        stop.Cancel();
        try { await server; } catch { }

        string StepAt(double t) => timeline.LastOrDefault(x => x.T <= t).Step ?? "?";
        Console.WriteLine("step over time: " + string.Join(" ", timeline.Where((x, i) => i % 5 == 0).Select(x => $"{x.T:0}s:{x.Step}")));
        var steadyLag = timeline.Where(x => x.State == LiveStreamer.StreamState.Live).Select(x => x.Lag).DefaultIfEmpty(0).Max();
        Console.WriteLine($"max lag while live: {steadyLag:0.0} s · health CSV: {csv} ({File.ReadLines(csv).Count() - 1} rows)");

        var fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"   {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) fails++; }
        if (kind == "connect")
        {
            var steps = timeline.Where(x => x.State == LiveStreamer.StreamState.Live).Select(x => x.Step).Distinct().ToList();
            Check(!steps.Any(s => s is "480p" or "360p" or "240p" or "720p lite"), $"slow connect isn't mistaken for a slow upload ({string.Join(",", steps)})");
            Check(StepAt(seconds - 1) == "1080p", $"reaches 1080p ({StepAt(seconds - 1)})");
        }
        else if (kind == "stall")
        {
            Check(File.ReadAllText(logPath).Contains("Upload stalled."), "detects an upload with no FFmpeg output progress");
            Check(timeline.Any(x => x.T is > 15 and < 30 && x.State == LiveStreamer.StreamState.Reconnecting),
                "reports reconnecting during the stalled upload");
            Check(timeline.Any(x => x.T > 30 && x.State == LiveStreamer.StreamState.Live && x.Step == "720p"),
                "resumes sending at the same quality after the link recovers");
        }
        else if (kind == "outage")
        {
            var before = StepAt(39);
            var lowest = timeline.Where(x => x.T >= 40 && x.T < 70).Select(x => Array.FindIndex(StreamRatePolicy.Ladder, s => s.Name == x.Step)).DefaultIfEmpty(0).Max();
            Check(lowest <= Array.FindIndex(StreamRatePolicy.Ladder, s => s.Name == before), $"no quality drop from the outage (before {before})");
        }
        else
        {
            var duringDrop = timeline.Where(x => x.T >= 45 && x.T < 65).Select(x => x.Step).Distinct().ToList();
            Check(duringDrop.All(s => s is "480p" or "360p" or "240p" or "720p lite"), $"ran a step that fits 1.5 Mbps during the drop ({string.Join(",", duringDrop)})");
            var high = timeline.Where(x => x.T > 65).Select(x => Array.FindIndex(StreamRatePolicy.Ladder, s => s.Name == x.Step)).DefaultIfEmpty(9).Min();
            Check(high <= Array.FindIndex(StreamRatePolicy.Ladder, s => s.Name == "720p"), $"climbed back to 720p or better after recovery ({StreamRatePolicy.Ladder[Math.Min(high, StreamRatePolicy.Ladder.Length - 1)].Name})");
        }
        if (kind != "stall") Check(steadyLag <= 3.2, "stayed within ~3 s of real time");
        Console.WriteLine(fails == 0 ? "PASS" : "FAIL");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>TCP proxy listenPort → targetPort whose upstream bandwidth follows <paramref name="mbps"/> (0 = outage).</summary>
    public static async Task ScheduledProxy(int listenPort, int targetPort, Func<double> mbps, TimeSpan connectDelay, CancellationToken ct)
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
                        if (connectDelay > TimeSpan.Zero) await Task.Delay(connectDelay, ct);
                        await upstream.ConnectAsync(IPAddress.Loopback, targetPort, ct);
                        c.ReceiveBufferSize = 16 * 1024;
                        var a = c.GetStream();
                        var b = upstream.GetStream();
                        _ = b.CopyToAsync(a, ct);
                        // Token bucket: credit accrues at the scheduled rate (timer-granularity proof).
                        var buf = new byte[16384];
                        var clock = Stopwatch.StartNew();
                        double last = 0, credit = 0;
                        while (!ct.IsCancellationRequested)
                        {
                            var now = clock.Elapsed.TotalSeconds;
                            credit = Math.Min(credit + mbps() * 1_000_000 / 8 * (now - last), 32 * 1024);
                            last = now;
                            if (credit < 1024) { await Task.Delay(5, ct); continue; } // 0 Mbps = outage: sender blocks
                            var r = await a.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, credit)), ct);
                            if (r == 0) break;
                            await b.WriteAsync(buf.AsMemory(0, r), ct);
                            credit -= r;
                        }
                    }
                    catch { }
                }, ct);
            }
        }
        catch { }
    }
}
