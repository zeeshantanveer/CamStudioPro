namespace CamStudio.Desktop.Media;

/// <summary>
/// Decides the live stream's quality step from what the uplink actually delivers. Pure logic (no I/O, the
/// caller supplies the clock), so it is unit-testable with simulated networks.
///
/// Inputs each tick (about once a second):
/// - <c>lag</c>: how far the stream is behind real time. It grows only when the upload can't keep up.
/// - <c>deliveredKbps</c>: bytes actually written to the network per second (ffmpeg's total_size). While
///   a backlog exists this is the real uplink capacity; otherwise it is just the stream's own bitrate.
///
/// Policy (asymmetric on purpose, as recommended for ABR controllers):
/// - Down fast: the backlog is growing (&gt; 0.1 s per second over 3 s, from 1 s behind) or it reached
///   2.5 s. Jump straight to the highest step that fits 80 % of the measured uplink.
/// - Outage is not slowness: if almost nothing is being delivered, keep the quality and wait. When data
///   flows again but the stream is still behind, reconnect at the same quality to drop the backlog.
/// - Up slowly: after 60 s with no lag, try one step higher. If that upgrade fails within 45 s, go straight
///   back to the step that worked and double the wait before trying that higher step again (2, 4, 8 … up to
///   10 min). Waits are remembered per step, so climbing back through lower steps doesn't reset them.
/// </summary>
public sealed class StreamRatePolicy
{
    /// <summary>One quality step. Bitrates are within YouTube's recommended ranges for 30 fps.</summary>
    public sealed record Step(string Name, int ShortSide, int Kbps);

    public static readonly Step[] Ladder =
    {
        new("4K", 2160, 20000),
        new("1440p", 1440, 9000),
        new("1080p", 1080, 4500),
        new("720p", 720, 2500),
        new("720p lite", 720, 1500),
        new("480p", 480, 1000),
        new("360p", 360, 600),
        new("240p", 240, 300),
    };

    public sealed class Options
    {
        public double GraceSeconds { get; init; } = 6;          // connecting briefly stalls input; don't judge it
        public double HardLagSeconds { get; init; } = 2.5;
        public double EarlyLagSeconds { get; init; } = 1.0;
        public double EarlySlope { get; init; } = 0.10;          // seconds of lag gained per second
        public double SlopeWindowSeconds { get; init; } = 3;
        public double SafetyFactor { get; init; } = 0.80;
        public int AudioKbps { get; init; } = 128;
        public double OutageRatio { get; init; } = 0.15;         // delivering < 15 % of the bitrate = outage
        public double StableLagSeconds { get; init; } = 0.3;
        public double StableSecondsBeforeUpgrade { get; init; } = 60;
        public double FailedUpgradeWindowSeconds { get; init; } = 45;
        public double MaxUpgradeWaitSeconds { get; init; } = 600;
        public double ThroughputAlpha { get; init; } = 0.35;     // EWMA weight of the newest sample
        public double OutageMemorySeconds { get; init; } = 5;    // a near-zero second within this window = outage
    }

    public enum Kind { None, Switch, Restart }

    public readonly record struct Decision(Kind Kind, int Tier, string Reason)
    {
        public static readonly Decision Hold = new(Kind.None, -1, "");
    }

    private readonly Options _o;
    private readonly bool _allowUpgrades;
    private readonly int _bestAllowed; // lowest index (highest quality) we may climb to
    private readonly Queue<(double T, double Lag)> _lags = new();
    private readonly Queue<(double T, double Kbps)> _delivered = new();
    private double _sessionStart, _stableSince;
    private double _ewma = double.NaN;
    // Upgrade probing: the step we last climbed to (and from), and per-step wait after failed attempts.
    private double? _lastUpgradeAt;
    private int _upgradeFrom = -1;
    private readonly double[] _waitFor = new double[Ladder.Length];
    private readonly double[] _failedAt = Enumerable.Repeat(double.NegativeInfinity, Ladder.Length).ToArray();

    public int Tier { get; private set; }
    /// <summary>The highest step that held through a full trial (or the start step until one does).</summary>
    public int StableTier { get; private set; }
    public Step Current => Ladder[Tier];
    /// <summary>Smoothed delivered throughput in kbps (NaN until measured).</summary>
    public double DeliveredKbps => _ewma;
    /// <summary>Recent lag trend in seconds per second.</summary>
    public double LagSlope { get; private set; }
    /// <summary>Wait after a failed attempt before trying the next higher step again (seconds).</summary>
    public double UpgradeWaitSeconds => Tier > 0 ? _waitFor[Tier - 1] : 0;

    public StreamRatePolicy(int startTier, int bestAllowedTier, Options? options = null, bool allowUpgrades = true)
    {
        _allowUpgrades = allowUpgrades;
        _o = options ?? new Options();
        _bestAllowed = Math.Clamp(bestAllowedTier, 0, Ladder.Length - 1);
        Tier = Math.Clamp(startTier, _bestAllowed, Ladder.Length - 1);
        StableTier = Tier;
    }

    /// <summary>
    /// Start and best steps. A named quality is the most the stream may use. Auto may climb to the phone's
    /// own resolution (up to 4K), and starts at the step that last held (if known) or 720p.
    /// </summary>
    public static (int Start, int Best) Choose(string quality, int srcWidth, int srcHeight, string? lastStableStep = null)
    {
        var srcShort = Math.Min(srcWidth, srcHeight);
        int Fit(int i)
        {
            while (i < Ladder.Length - 1 && srcShort > 0 && Ladder[i].ShortSide > srcShort) i++;
            return i;
        }
        var chosen = Array.FindIndex(Ladder, t => t.Name == quality);
        if (chosen >= 0)
            return (Fit(chosen), Fit(chosen));
        var p720 = Array.FindIndex(Ladder, t => t.Name == "720p");
        var last = Array.FindIndex(Ladder, t => t.Name == lastStableStep);
        // Auto: start where the last stream held (never above 1080p: a new place or network may be slower),
        // otherwise at 720p; climb as far as the phone's resolution when the link proves it can.
        var p1080 = Array.FindIndex(Ladder, t => t.Name == "1080p");
        var start = last >= 0 ? Math.Max(last, p1080) : p720;
        return (Fit(start), Fit(0));
    }

    /// <summary>Call when a publish session (re)connects.</summary>
    public void SessionStarted(double now)
    {
        _sessionStart = now;
        _stableSince = now;
        _lags.Clear();
        _delivered.Clear();
        LagSlope = 0;
        _ewma = double.NaN;
    }

    public Decision Tick(double now, double lag, double deliveredKbps)
    {
        if (double.IsFinite(deliveredKbps) && deliveredKbps >= 0)
        {
            _ewma = double.IsNaN(_ewma) ? deliveredKbps : _o.ThroughputAlpha * deliveredKbps + (1 - _o.ThroughputAlpha) * _ewma;
            _delivered.Enqueue((now, deliveredKbps));
        }
        while (_delivered.Count > 0 && now - _delivered.Peek().T > _o.OutageMemorySeconds)
            _delivered.Dequeue();

        _lags.Enqueue((now, lag));
        while (_lags.Count > 1 && now - _lags.Peek().T > _o.SlopeWindowSeconds)
            _lags.Dequeue();
        var (t0, l0) = _lags.Peek();
        LagSlope = now - t0 >= _o.SlopeWindowSeconds * 0.8 ? (lag - l0) / (now - t0) : 0;

        if (now - _sessionStart < _o.GraceSeconds)
            return Decision.Hold;
        // No measurement yet (or none recently): never act on guesses.
        if (double.IsNaN(_ewma) || _delivered.Count == 0)
            return Decision.Hold;

        // An upgrade that held for the whole trial window succeeded: the link can carry that step.
        if (_lastUpgradeAt is double up && now - up > _o.FailedUpgradeWindowSeconds)
        {
            _lastUpgradeAt = null;
            _waitFor[Tier] = 0;
        }
        if (_lastUpgradeAt == null && now - _sessionStart > _o.FailedUpgradeWindowSeconds && lag <= _o.StableLagSeconds)
            StableTier = Tier; // held for a full trial window

        var need = Current.Kbps + _o.AudioKbps;
        var congested = lag >= _o.HardLagSeconds || (lag >= _o.EarlyLagSeconds && LagSlope >= _o.EarlySlope);
        if (congested)
        {
            // Outage or hiccup: lowering quality wouldn't help, so wait; once data flows at full rate again,
            // reconnect at the same step to drop the backlog. It's a dropout when near-zero seconds are mixed
            // with good ones, or the link is dead. Steady low throughput is a capacity shortage instead.
            var anyNearZero = _delivered.Any(d => d.Kbps < need * _o.OutageRatio);
            var outage = anyNearZero && (_delivered.Any(d => d.Kbps >= need * 0.5) || _delivered.All(d => d.Kbps < 64));
            var latest = _delivered.Count > 0 ? _delivered.Last().Kbps : 0;
            if (outage)
                return lag >= _o.HardLagSeconds && latest >= need * 0.9
                    ? new Decision(Kind.Restart, Tier, "caught up after a network dropout")
                    : Decision.Hold;

            var delivered = double.IsNaN(_ewma) ? 0 : _ewma;
            if (delivered >= need * 1.05)
            {
                // The link carries this step again (backlog draining). Only reconnect if far enough behind.
                return lag >= _o.HardLagSeconds ? new Decision(Kind.Restart, Tier, "caught up after a network hiccup") : Decision.Hold;
            }

            int target;
            if (_lastUpgradeAt != null && _upgradeFrom > Tier)
            {
                // A trial of a higher step failed: go back to the step that was working, and wait longer
                // (2, 4, 8 … min) before trying this step again.
                _waitFor[Tier] = Math.Min(Math.Max(_o.StableSecondsBeforeUpgrade * 2, _waitFor[Tier] * 2), _o.MaxUpgradeWaitSeconds);
                _failedAt[Tier] = now;
                target = _upgradeFrom;
                _lastUpgradeAt = null;
                Tier = target;
                return new Decision(Kind.Switch, target, $"upload ~{delivered / 1000:0.0} Mbps, back to the step that worked");
            }

            // Real capacity shortage: jump to the highest step that fits the measured upload with a margin.
            target = Ladder.Length - 1;
            for (var i = Tier + 1; i < Ladder.Length; i++)
            {
                if (Ladder[i].Kbps + _o.AudioKbps <= _o.SafetyFactor * delivered) { target = i; break; }
            }
            if (target == Tier) // already at the lowest step
                return lag >= _o.HardLagSeconds ? new Decision(Kind.Restart, Tier, "upload too slow even at the lowest quality") : Decision.Hold;
            // Steps above the new one are now known not to fit: don't retry them right away.
            for (var i = target - 1; i >= 0; i--)
                if (_waitFor[i] < _o.StableSecondsBeforeUpgrade) { _waitFor[i] = _o.StableSecondsBeforeUpgrade; _failedAt[i] = now; }
            Tier = target;
            return new Decision(Kind.Switch, target, $"upload ~{delivered / 1000:0.0} Mbps");
        }

        if (lag > _o.StableLagSeconds)
            _stableSince = now;
        var next = Tier - 1;
        if (_allowUpgrades && Tier > _bestAllowed && _lastUpgradeAt == null
            && now - _stableSince >= _o.StableSecondsBeforeUpgrade
            && now - _failedAt[next] >= _waitFor[next])
        {
            _upgradeFrom = Tier;
            Tier = next;
            _lastUpgradeAt = now;
            return new Decision(Kind.Switch, Tier, "upload stable, trying higher quality");
        }
        return Decision.Hold;
    }
}
