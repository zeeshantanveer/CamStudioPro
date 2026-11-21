using CamStudio.Desktop.Media;
using CamStudio.Desktop.Core;

/// <summary>
/// --policy: deterministic network simulation of StreamRatePolicy (no ffmpeg, runs in a second).
/// Model: the stream produces (step kbps + audio) per second; the uplink carries up to capacity C(t);
/// the difference accumulates as backlog (lag). A quality change or restart reconnects: backlog is
/// dropped and viewers see a 2 s gap.
/// </summary>
static class PolicySim
{
    private sealed record Result(string Name, double MaxLagSteady, int Switches, int Restarts, double GapSeconds,
        string FinalStep, List<(double T, string Step, string Kind)> Events);

    private static Result Run(string name, Func<double, double> capacityKbps, double seconds, string quality = "Auto", bool allowUpgrades = true)
    {
        var (start, best) = StreamRatePolicy.Choose(quality, 1920, 1080);
        var policy = new StreamRatePolicy(start, best, allowUpgrades: allowUpgrades);
        const double dt = 0.5, gap = 2;
        double lag = 0, gapUntil = 0, maxLag = 0, gapTotal = 0;
        int switches = 0, restarts = 0;
        var events = new List<(double, string, string)>();
        policy.SessionStarted(0);
        for (double t = 0; t < seconds; t += dt)
        {
            if (t < gapUntil) { gapTotal += dt; continue; }
            var need = policy.Current.Kbps + 128.0;
            var backlogBits = lag * need;
            var produced = need * dt;
            var carried = capacityKbps(t) * dt;
            var newBacklog = Math.Max(0, backlogBits + produced - carried);
            var delivered = (backlogBits + produced - newBacklog) / dt;
            lag = newBacklog / need;
            if (t > 10) maxLag = Math.Max(maxLag, lag);
            if (Math.Abs(t % 1) > 1e-9) continue; // policy ticks once a second
            var d = policy.Tick(t, lag, delivered);
            if (d.Kind == StreamRatePolicy.Kind.None) continue;
            if (d.Kind == StreamRatePolicy.Kind.Switch) switches++; else restarts++;
            events.Add((t, policy.Current.Name, d.Kind == StreamRatePolicy.Kind.Restart ? "restart" : d.Reason));
            lag = 0;
            gapUntil = t + gap;
            policy.SessionStarted(gapUntil);
        }
        return new Result(name, maxLag, switches, restarts, gapTotal, policy.Current.Name, events);
    }

    public static int RunAll()
    {
        var failures = 0;
        void Check(bool ok, string what) { Console.WriteLine($"   {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) failures++; }
        void Show(Result r)
        {
            Console.WriteLine($"{r.Name}: final {r.FinalStep}, {r.Switches} switches, {r.Restarts} restarts, {r.GapSeconds:0} s of gaps, max lag {r.MaxLagSteady:0.0} s");
            foreach (var (t, step, kind) in r.Events.Take(12)) Console.WriteLine($"     {t,5:0}s → {step,-9} {kind}");
            if (r.Events.Count > 12) Console.WriteLine($"     … {r.Events.Count - 12} more");
        }
        string StepAt(Result r, double t) => r.Events.LastOrDefault(e => e.T <= t).Step ?? "720p";

        // A. Steady 3 Mbps: 720p (2.6 Mbps) fits, 1080p (4.6) doesn't. Should settle on 720p and stop probing often.
        var a = Run("A steady 3 Mbps, 10 min", _ => 3000, 600);
        Show(a);
        Check(a.FinalStep == "720p", "settles on 720p");
        Check(a.MaxLagSteady <= 3, "lag stays ≤ 3 s");
        Check(a.Switches + a.Restarts <= 8, "no oscillation (≤ 8 reconnects in 10 min)");

        // B. Variable: 6 Mbps, drop to 1.5 Mbps at 120 s, back to 6 Mbps at 240 s.
        var b = Run("B variable 6 → 1.5 → 6 Mbps", t => t < 120 ? 6000 : t < 240 ? 1500 : 6000, 600);
        Show(b);
        var down = b.Events.FirstOrDefault(e => e.T >= 120 && e.T < 240 && e.Kind.StartsWith("upload ~"));
        Check(down != default && down.T - 120 <= 8, $"steps down within 8 s of the drop ({(down == default ? "none" : $"{down.T - 120:0} s")})");
        Check(StepAt(b, 239) is "480p" or "360p" or "720p lite", $"runs a step that fits 1.5 Mbps during the drop ({StepAt(b, 239)})");
        var backUp = b.Events.FirstOrDefault(e => e.T > 240 && e.Step == "1080p");
        Check(backUp != default && backUp.T - 240 <= 300, $"climbs back to 1080p after recovery ({(backUp == default ? "never" : $"{backUp.T - 240:0} s")})");
        Check(b.MaxLagSteady <= 3, "lag stays ≤ 3 s");

        // C. Outage: 6 Mbps with a 4 s total outage at 150 s. Quality must not drop because of it.
        var c = Run("C 4 s outage", t => t >= 150 && t < 154 ? 0 : 6000, 300);
        Show(c);
        Check(!c.Events.Any(e => e.T >= 150 && e.T < 170 && e.Kind.StartsWith("upload ~")), "no quality drop caused by the outage");
        Check(c.Restarts <= 1, "at most one catch-up reconnect");

        // D. Slow 0.5 Mbps: only 240p (0.43 Mbps with audio) fits. It should get there and stay, without restarts.
        var d = Run("D slow 0.5 Mbps", _ => 500, 300);
        Show(d);
        Check(d.FinalStep == "240p", "uses 240p");
        Check(d.MaxLagSteady <= 3.2 && d.Restarts == 0, "lag bounded, no restarts once on 240p");

        // D2. Hopeless 0.25 Mbps: nothing fits. Lowest step; restarts keep viewers within a few seconds of live.
        var d2 = Run("D2 hopeless 0.25 Mbps", _ => 250, 300);
        Show(d2);
        Check(d2.FinalStep == "240p" && d2.MaxLagSteady <= 3.2, "lowest step, lag bounded");

        // E. Fast 10 Mbps: one upgrade to 1080p, then nothing.
        var e = Run("E fast 10 Mbps", _ => 10000, 600);
        Show(e);
        Check(e.FinalStep == "1080p" && e.Switches == 1 && e.Restarts == 0, "one upgrade to 1080p and stable");

        // F. User picked "Up to 720p" on a fast link: never goes above 720p.
        var f = Run("F 'Up to 720p' on 10 Mbps", _ => 10000, 300, "720p");
        Show(f);
        Check(f.FinalStep == "720p" && f.Switches == 0, "respects the user's maximum");

        var steady = Run("YouTube steady 6 Mbps", _ => 6000, 240, allowUpgrades: false);
        Check(steady.Switches == 0 && steady.Restarts == 0 && steady.FinalStep == "720p",
            "YouTube holds its starting quality without reconnecting to probe higher tiers");
        var drop = Run("YouTube sustained bandwidth loss", t => t < 60 || t >= 140 ? 6000 : 1300, 300,
            allowUpgrades: false);
        Check(drop.Switches == 1 && drop.FinalStep is "480p" or "360p",
            "YouTube adapts to a real capacity drop without probing back up mid-broadcast");
        using var youtube = new LiveStreamer("ffmpeg", "libx264", "rtmps://a.rtmps.youtube.com/live2/test",
            1920, 1080, 30, "Auto", lastStableStep: "1080p");
        Check(youtube.IsYouTube && youtube.Current.Name == "720p"
            && !LiveStreamer.IsYouTubeEndpoint("rtmp://notyoutube.com/live/test"),
            "YouTube auto starts at most at 720p and endpoint matching requires a host boundary");

        const string id = "abcdefghijk";
        const string endedPage = """
            <script>var ytInitialPlayerResponse = {"videoDetails":{"videoId":"abcdefghijk"},"microformat":{"playerMicroformatRenderer":{"liveBroadcastDetails":{"endTimestamp":"2026-11-19T16:33:54Z"}}}};</script>
            """;
        var parser = typeof(YouTubeBroadcast).GetMethod("HasEnded",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        bool HasEnded(string page, string videoId) => (bool)parser.Invoke(null, [page, videoId])!;
        Check(HasEnded(endedPage, id) && !HasEnded(endedPage, "abcdefghijx")
            && !HasEnded(endedPage.Replace("endTimestamp", "startTimestamp"), id)
            && !HasEnded("<script>ytInitialPlayerResponse = {};</script>", id)
            && !HasEnded("<script>ytInitialPlayerResponse = broken;</script>", id),
            "only blocks the matching event when the watch page confirms it ended");
        Check(YouTubeBroadcast.PreferSecureServer("rtmp://a.rtmp.youtube.com/live2/") == YouTubeBroadcast.SecureServer
            && YouTubeBroadcast.PreferSecureServer("rtmp://custom.example/live") == "rtmp://custom.example/live",
            "uses RTMPS for the built-in YouTube destination and preserves custom servers");

        Console.WriteLine(failures == 0 ? "PASS" : $"FAIL ({failures})");
        return failures == 0 ? 0 : 1;
    }
}
