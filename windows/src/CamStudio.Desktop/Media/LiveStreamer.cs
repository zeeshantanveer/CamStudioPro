using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;

namespace CamStudio.Desktop.Media;

/// <summary>
/// Streams the phone's picture and (enhanced) microphone live to YouTube or any RTMP(S) server.
/// Frames and PCM go to ffmpeg through two named pipes; ffmpeg encodes H.264 + AAC and publishes FLV.
///
/// Real time first:
/// - Output is paced by a wall clock at a constant frame rate. If the phone stalls or disconnects, the
///   stream keeps going with a black picture and silence so the broadcast doesn't end.
/// - ffmpeg's input queue is tiny, so when the upload can't keep up the backlog shows up here within
///   seconds (instead of silently growing inside ffmpeg). <see cref="StreamRatePolicy"/> watches that
///   backlog and the bytes really delivered, steps quality down fast and back up slowly; each change
///   reconnects, which also throws any backlog away.
/// - Every second is logged to an optional CSV (health telemetry) for diagnosing real broadcasts.
/// - Network failures restart ffmpeg automatically a few times.
/// </summary>
public sealed class LiveStreamer : IDisposable
{
    public enum StreamState { Connecting, Live, Reconnecting, Stopped, Failed }

    private const int SampleRate = 48000;
    private const int MaxRetries = 5;

    private readonly string _ffmpeg, _encoder, _url;
    private readonly bool _portrait;
    private readonly int _fps;
    private readonly object _gate = new();
    private readonly Queue<byte[]> _audio = new();
    private int _audioBytes, _headOffset;
    // Phone frames waiting to go out, in arrival order. A short queue absorbs uneven arrival (the decoder
    // hands frames over in small bursts) so every frame is sent once, instead of skip-one/repeat-one judder.
    private readonly Queue<Nv12Frame> _frames = new();
    private const int MaxQueuedFrames = 3;  // steady state: ~100 ms at 30 fps, smooth without adding delay
    private const int MaxStallFrames = 15;  // during a short stall (encoder start-up, network hiccup) keep ~0.5 s
    private long _latestTicks;
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _onAir = new();
    private Task? _run;
    private readonly StreamRatePolicy _policy;
    private readonly Stopwatch _policyClock = Stopwatch.StartNew();
    private double _videoLag; // seconds the video writer is behind the clock
    private readonly string? _logPath, _csvPath;
    private long _totalBytes = -1; // FFmpeg output byte counter, before YouTube playback
    private long _outputTimeUs;
    private const double UploadStallSeconds = 8;
    // Writer time base. Pacing starts only once the publish is connected (connecting to YouTube takes
    // several seconds, during which ffmpeg reads nothing); until then the backlog isn't real lag.
    private double _timeBase;
    private long _videoWritten, _audioWritten;
    private long _videoFillTo, _audioFillTo; // after connecting: fill the gap with filler up to here
    private int _srcW, _srcH;
    private const double ConnectTimeoutSeconds = 25;

    public bool IsYouTube { get; }
    public StreamState State { get; private set; } = StreamState.Connecting;
    public string? LastError { get; private set; }
    /// <summary>A short note for the user, e.g. that quality was lowered because the upload is slow.</summary>
    public string? Notice { get; private set; }
    public TimeSpan Elapsed => _onAir.Elapsed;
    public StreamRatePolicy.Step Current => _policy.Current;
    /// <summary>Measured upload throughput in kbps (NaN until known).</summary>
    public double UploadKbps => _policy.DeliveredKbps;
    public bool PhoneSignal => Environment.TickCount64 - Interlocked.Read(ref _latestTicks) < 1500;
    /// <summary>The highest step that has held for a full trial (to start the next stream there).</summary>
    public string StableStepName => StreamRatePolicy.Ladder[_policy.StableTier].Name;
    /// <summary>Seconds behind real time (0 when keeping up).</summary>
    public double Lag => Volatile.Read(ref _videoLag);
    /// <summary>Latest measured output bitrate in kbps from ffmpeg's progress report (0 = unknown).</summary>
    public double SentKbps { get; private set; }
    public event Action? Changed;

    /// <param name="srcWidth">Phone picture width (orientation decides landscape/portrait output).</param>
    /// <param name="quality">"Auto" (starts at 720p, adapts between 360p and 1080p) or a step name (the most it may use).</param>
    /// <param name="csvPath">Optional per-second health log (CSV).</param>
    public LiveStreamer(string ffmpeg, string encoder, string url, int srcWidth, int srcHeight, int fps, string quality,
        string? logPath = null, string? csvPath = null, StreamRatePolicy.Options? policy = null, string? lastStableStep = null)
    {
        _ffmpeg = ffmpeg;
        _encoder = encoder;
        _url = url;
        _portrait = srcHeight > srcWidth;
        _fps = Math.Clamp(fps, 15, 60);
        _logPath = logPath;
        _csvPath = csvPath;
        _srcW = srcWidth;
        _srcH = srcHeight;
        IsYouTube = IsYouTubeEndpoint(url);
        var (start, best) = StreamRatePolicy.Choose(quality, srcWidth, srcHeight, lastStableStep);
        if (IsYouTube && quality == "Auto")
        {
            var (defaultStart, _) = StreamRatePolicy.Choose(quality, srcWidth, srcHeight);
            start = Math.Max(defaultStart, start);
        }
        // Each tier change tears down RTMP. Keep YouTube sessions at their working tier
        // unless there is a sustained capacity shortage.
        _policy = new StreamRatePolicy(start, best,
            policy ?? (IsYouTube ? new StreamRatePolicy.Options { OutageMemorySeconds = 8 } : null),
            allowUpgrades: !IsYouTube);
    }

    public static bool IsYouTubeEndpoint(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("youtube-video.google.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Output size for a tier, 16:9 in the phone's orientation, even dimensions.</summary>
    private (int Width, int Height) Size(StreamRatePolicy.Step t)
    {
        var shortSide = t.ShortSide;
        var longSide = (shortSide * 16 / 9) & ~1;
        return _portrait ? (shortSide, longSide) : (longSide, shortSide);
    }

    public void PushVideo(Nv12Frame frame)
    {
        _srcW = frame.Width;
        _srcH = frame.Height;
        lock (_gate)
        {
            _frames.Enqueue(frame);
            while (_frames.Count > MaxStallFrames)
                _frames.Dequeue(); // never let the picture fall far behind real time
        }
        Interlocked.Exchange(ref _latestTicks, Environment.TickCount64);
    }

    /// <summary>48 kHz mono 16-bit PCM from the phone (after voice enhancement).</summary>
    public void PushAudio(ReadOnlySpan<byte> pcm)
    {
        lock (_gate)
        {
            _audio.Enqueue(pcm.ToArray());
            _audioBytes += pcm.Length;
            // Keep at most ~0.5 s queued so audio can't drift behind the picture.
            while (_audioBytes > SampleRate && _audio.Count > 1)
            {
                _audioBytes -= _audio.Dequeue().Length - _headOffset;
                _headOffset = 0;
            }
        }
    }

    public void Start() => _run ??= Task.Run(RunAsync);

    public async Task StopAsync()
    {
        _stop.Cancel();
        if (_run != null)
        {
            try { await _run.ConfigureAwait(false); } catch { }
        }
        _onAir.Stop();
        Log("stopped by user");
        SetState(StreamState.Stopped, null);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private void SetState(StreamState state, string? error)
    {
        State = state;
        LastError = error;
        Changed?.Invoke();
    }

    private void Log(string line)
    {
        if (_logPath == null) return;
        try { File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}"); } catch { }
    }

    /// <summary>How a publish session ended: a planned reconnect (quality change / catch-up) or an error.</summary>
    private sealed record Outcome(StreamRatePolicy.Decision? Planned, string? Error);

    private async Task RunAsync()
    {
        Log($"start: encoder={_encoder} fps={_fps} tier={Current.Name} url={RedactKey(_url)}");
        WriteCsvHeader();
        var failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            var started = Environment.TickCount64;
            Outcome outcome;
            try
            {
                outcome = await RunOnceAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                outcome = new Outcome(null, e.Message);
            }
            if (_stop.IsCancellationRequested)
                return;

            if (outcome.Planned is { } plan)
            {
                // Planned reconnect: new quality, or dropping a backlog after a network hiccup.
                Notice = plan.Kind == StreamRatePolicy.Kind.Restart
                    ? $"Reconnected to stay live in real time ({plan.Reason})."
                    : plan.Reason.StartsWith("upload stable")
                        ? $"Upload looks good: trying {Current.Name}."
                        : $"Upload is slower ({plan.Reason}): switched to {Current.Name}.";
                Log($"{plan.Kind} → {Current.Name}: {plan.Reason}");
                SetState(StreamState.Reconnecting, null);
                continue;
            }

            // A session that stayed up for a while resets the retry budget.
            failures = Environment.TickCount64 - started > 60_000 ? 1 : failures + 1;
            Log($"ended: {outcome.Error} (attempt {failures})");
            if (failures > MaxRetries)
            {
                _onAir.Stop();
                SetState(StreamState.Failed, Explain(outcome.Error));
                return;
            }
            SetState(StreamState.Reconnecting, Explain(outcome.Error));
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 2 * failures)), _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Runs one ffmpeg publish session.</summary>
    private async Task<Outcome> RunOnceAsync()
    {
        var tier = Current;
        var (width, height) = Size(tier);
        var id = Guid.NewGuid().ToString("N");
        var videoName = $"camstudio-live-v-{id}";
        var audioName = $"camstudio-live-a-{id}";
        var frameBytes = width * height * 3 / 2;
        using var videoPipe = new NamedPipeServerStream(videoName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, frameBytes);
        using var audioPipe = new NamedPipeServerStream(audioName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, SampleRate / 5);

        var psi = new ProcessStartInfo(_ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in Arguments($@"\\.\pipe\{videoName}", $@"\\.\pipe\{audioName}", width, height, tier.Kbps))
            psi.ArgumentList.Add(a);
        Log($"publish {width}x{height} {tier.Kbps}k");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg didn't start.");
        string? lastLine = null;
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lastLine = e.Data.Trim();
            Log("ffmpeg: " + lastLine);
        };
        process.OutputDataReceived += (_, e) => ParseProgress(e.Data);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var clock = Stopwatch.StartNew();
        Volatile.Write(ref _videoLag, 0);
        Volatile.Write(ref _timeBase, 0);
        Interlocked.Exchange(ref _videoWritten, 0);
        Interlocked.Exchange(ref _audioWritten, 0);
        Interlocked.Exchange(ref _videoFillTo, 0);
        Interlocked.Exchange(ref _audioFillTo, 0);
        Interlocked.Exchange(ref _totalBytes, -1);
        Interlocked.Exchange(ref _outputTimeUs, 0);
        SentKbps = 0;
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        var connected = false;
        var timedOut = false;
        double lastProgress = 0;
        long observedBytes = -1, observedMedia = 0;
        double lastTick = 0;
        long lastBytes = -1;
        var video = Task.Run(() => WriteVideoAsync(videoPipe, clock, width, height, session.Token));
        var audio = Task.Run(() => WriteAudioAsync(audioPipe, clock, session.Token));
        var exited = process.WaitForExitAsync(CancellationToken.None);

        StreamRatePolicy.Decision? planned = null;
        while (!session.IsCancellationRequested)
        {
            var tick = Task.Delay(500, session.Token);
            var first = await Task.WhenAny(exited, video, audio, tick).ConfigureAwait(false);
            if (first != tick)
                break;
            var t = clock.Elapsed.TotalSeconds;
            var bytes = Interlocked.Read(ref _totalBytes);
            var mediaUs = Interlocked.Read(ref _outputTimeUs);
            if (bytes > observedBytes && mediaUs > observedMedia)
            {
                observedBytes = bytes;
                observedMedia = mediaUs;
                lastProgress = t;
            }
            if (!connected)
            {
                // Require audio/video progress as well as bytes. The FLV header alone
                // only indicates that the RTMP handshake completed.
                if (lastProgress > 0)
                {
                    connected = true;
                    // Re-base both writers on "now", at the same media time, so the connect wait isn't
                    // counted as lag and audio stays aligned with video: the writer that's behind fills
                    // up to the common point at once, the other waits for the clock.
                    lock (_gate)
                    {
                        // Drop content captured during the connect wait from both queues, so the gap is
                        // filled with the last picture and silence and both resume with "now" together.
                        _frames.Clear();
                        _audio.Clear();
                        _audioBytes = 0;
                        _headOffset = 0;
                        // Common media time, on a frame boundary. Each writer fills up to it with filler (last
                        // picture / silence) without touching its queue, then both continue with live content.
                        var media = Math.Ceiling(Math.Max(Interlocked.Read(ref _videoWritten) / (double)_fps,
                                                          Interlocked.Read(ref _audioWritten) / (double)SampleRate) * _fps) / _fps;
                        Interlocked.Exchange(ref _videoFillTo, (long)Math.Round(media * _fps));
                        Interlocked.Exchange(ref _audioFillTo, (long)Math.Round(media * SampleRate));
                        Volatile.Write(ref _timeBase, t - media);
                    }
                    _policy.SessionStarted(_policyClock.Elapsed.TotalSeconds);
                    lastTick = t;
                    lastBytes = Interlocked.Read(ref _totalBytes);
                    Log($"connected after {t:0.0}s");
                    if (!_onAir.IsRunning) _onAir.Start();
                    SetState(StreamState.Live, null);
                }
                else if (t > ConnectTimeoutSeconds)
                {
                    lastLine ??= "Couldn't send media to the streaming server (timed out).";
                    timedOut = true;
                    break;
                }
                continue;
            }
            if (t - lastProgress >= UploadStallSeconds)
            {
                lastLine = "Upload stalled. Check your internet connection if it keeps happening.";
                timedOut = true;
                Log(lastLine);
                break;
            }
            Changed?.Invoke(); // refresh stats in the UI
            // Once a second: measure FFmpeg output progress and let the policy decide.
            if (t - lastTick >= 1)
            {
                var kbps = bytes > 0 && lastBytes > 0 ? (bytes - lastBytes) * 8 / 1000.0 / (t - lastTick) : double.NaN;
                lastBytes = bytes;
                lastTick = t;
                var decision = _policy.Tick(_policyClock.Elapsed.TotalSeconds, Lag, kbps);
                WriteCsv(tier, kbps, decision);
                if (decision.Kind != StreamRatePolicy.Kind.None)
                {
                    planned = decision;
                    Log($"lag {Lag:0.0}s slope {_policy.LagSlope:+0.00;-0.00} upload {_policy.DeliveredKbps:0} kbps → {decision.Kind} {decision.Reason}");
                    break;
                }
            }
        }

        // Stop feeding; closing the pipes lets ffmpeg flush and end cleanly.
        session.Cancel();
        try { await Task.WhenAll(video, audio).ConfigureAwait(false); } catch { }
        try { videoPipe.Dispose(); audioPipe.Dispose(); } catch { }
        if (!process.HasExited)
        {
            // On a planned reconnect, don't wait for the backlog to drain: reconnect fresh.
            var done = planned != null || timedOut ? null : await Task.WhenAny(exited, Task.Delay(5000)).ConfigureAwait(false);
            if (done != exited)
            {
                try { process.Kill(true); } catch { }
                await Task.WhenAny(exited, Task.Delay(1500)).ConfigureAwait(false);
            }
        }
        if (_stop.IsCancellationRequested)
            return new Outcome(null, null);
        if (timedOut && connected)
            return new Outcome(null, "Upload stalled. Check your internet connection if it keeps happening.");
        return planned != null ? new Outcome(planned, null) : new Outcome(null, lastLine ?? $"ffmpeg stopped (code {SafeExitCode(process)}).");
    }

    private void ParseProgress(string? line)
    {
        // -progress pipe:1 prints key=value lines, e.g. bitrate=2493.1kbits/s, total_size=1234567
        if (line == null) return;
        if (line.StartsWith("total_size=", StringComparison.Ordinal))
        {
            if (long.TryParse(line.AsSpan("total_size=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes))
                Interlocked.Exchange(ref _totalBytes, bytes);
            return;
        }
        if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
        {
            if (long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeUs))
                Interlocked.Exchange(ref _outputTimeUs, timeUs);
            return;
        }
        if (!line.StartsWith("bitrate=", StringComparison.Ordinal)) return;
        var v = line["bitrate=".Length..].Replace("kbits/s", "").Trim();
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var kbps))
            SentKbps = kbps;
    }

    private void WriteCsvHeader()
    {
        if (_csvPath == null) return;
        try
        {
            if (!File.Exists(_csvPath))
                File.WriteAllText(_csvPath, "time,elapsed_s,state,tier,target_kbps,encoder_kbps,delivered_kbps,upload_est_kbps,lag_s,lag_slope,phone_video,source,decision" + Environment.NewLine);
        }
        catch { }
    }

    private void WriteCsv(StreamRatePolicy.Step publishingTier, double deliveredKbps, StreamRatePolicy.Decision decision)
    {
        if (_csvPath == null) return;
        static string F(double v) => double.IsFinite(v) ? v.ToString("0.###", CultureInfo.InvariantCulture) : "";
        var what = decision.Kind == StreamRatePolicy.Kind.None ? "" : $"{decision.Kind}:{StreamRatePolicy.Ladder[decision.Tier].Name}:{decision.Reason}".Replace(',', ';');
        var line = string.Join(",", DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), F(_policyClock.Elapsed.TotalSeconds),
            State, publishingTier.Name, publishingTier.Kbps, F(SentKbps), F(deliveredKbps), F(_policy.DeliveredKbps), F(Lag), F(_policy.LagSlope),
            PhoneSignal ? 1 : 0, $"{_srcW}x{_srcH}", what);
        try { File.AppendAllText(_csvPath, line + Environment.NewLine); } catch { }
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    private IEnumerable<string> Arguments(string videoPipe, string audioPipe, int width, int height, int kbps)
    {
        var gop = _fps * 2; // YouTube wants a keyframe at least every 2 s
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:1", "-stats_period", "1",
            // Raw pipes need no format detection. Without these, ffmpeg waits ~5 s analysing the audio
            // input before it reads any video, which freezes and then jumps the picture at every start.
            "-fflags", "nobuffer", "-probesize", "32", "-analyzeduration", "0",
            // Tiny input queues: backlog must show up in our writer, not hide inside ffmpeg.
            "-thread_queue_size", "4", "-f", "rawvideo", "-pix_fmt", "nv12", "-video_size", $"{width}x{height}",
            "-framerate", _fps.ToString(CultureInfo.InvariantCulture), "-i", videoPipe,
            "-probesize", "32", "-analyzeduration", "0",
            "-thread_queue_size", "16", "-f", "s16le", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture), "-ac", "1", "-i", audioPipe,
            "-map", "0:v", "-map", "1:a",
            "-c:v", _encoder,
        };
        args.AddRange(_encoder switch
        {
            "libx264" => new[] { "-preset", "veryfast", "-tune", "zerolatency", "-profile:v", "high" },
            "h264_nvenc" => new[] { "-preset", "p4", "-tune", "ll", "-rc", "cbr", "-bf", "0", "-rc-lookahead", "0", "-strict_gop", "1",
                "-no-scenecut", "1", "-multipass", "disabled", "-zerolatency", "1", "-profile:v", "high" },
            "h264_qsv" => new[] { "-preset", "veryfast", "-look_ahead", "0", "-profile:v", "high" },
            "h264_amf" => new[] { "-usage", "lowlatency", "-rc", "cbr" },
            "h264_mf" => new[] { "-rate_control", "cbr", "-scenario", "live_streaming" },
            _ => Array.Empty<string>(),
        });
        args.AddRange(new[]
        {
            "-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{kbps}k",
            "-g", gop.ToString(CultureInfo.InvariantCulture), "-keyint_min", gop.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2",
            "-flush_packets", "1",
            "-f", "flv", "-flvflags", "no_duration_filesize", _url,
        });
        return args;
    }

    private async Task WriteVideoAsync(NamedPipeServerStream pipe, Stopwatch clock, int width, int height, CancellationToken ct)
    {
        await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
        var buffer = new byte[width * height * 3 / 2];
        Nv12Frame? drawn = null;
        long written = 0;
        while (!ct.IsCancellationRequested)
        {
            // Frame k goes out once the clock reaches frame k−1. Pictures may sit up to one frame (33 ms) behind the
            // audio, far below what viewers notice, and the queue always has the next frame ready (no stutter).
            var due = (long)((clock.Elapsed.TotalSeconds - Volatile.Read(ref _timeBase)) * _fps) + 1;
            Volatile.Write(ref _videoLag, Math.Max(0, (due - written - 1) / (double)_fps));
            if (written < due)
            {
                Nv12Frame? frame = drawn;
                if (written < Interlocked.Read(ref _videoFillTo))
                {
                    // Connect gap: repeat the last picture without consuming live frames.
                    if (written == 0) Nv12Scaler.Fit(null, buffer, width, height);
                    await pipe.WriteAsync(buffer, ct).ConfigureAwait(false);
                    written++;
                    Interlocked.Exchange(ref _videoWritten, written);
                    continue;
                }
                lock (_gate)
                {
                    // When keeping up, hold the queue to ~100 ms. While catching up after a short stall,
                    // send the queued frames in order (real motion instead of repeats).
                    var catchingUp = due - written > 1;
                    while (!catchingUp && _frames.Count > MaxQueuedFrames)
                        _frames.Dequeue();
                    if (_frames.Count > 0)
                        frame = _frames.Dequeue();
                    else if (!PhoneSignal)
                        frame = null; // phone gone: black picture
                    // else: no new frame yet, repeat the last one
                }
                if (!ReferenceEquals(frame, drawn) || written == 0)
                {
                    Nv12Scaler.Fit(frame, buffer, width, height);
                    drawn = frame;
                }
                // Short catch-up bursts keep A/V aligned to the clock; a long backlog ends the session (see Lag).
                await pipe.WriteAsync(buffer, ct).ConfigureAwait(false);
                written++;
                Interlocked.Exchange(ref _videoWritten, written);
                continue;
            }
            var next = TimeSpan.FromSeconds((double)written / _fps + Volatile.Read(ref _timeBase)) - clock.Elapsed;
            if (next > TimeSpan.Zero)
                await Task.Delay(next, ct).ConfigureAwait(false);
        }
    }

    private async Task WriteAudioAsync(NamedPipeServerStream pipe, Stopwatch clock, CancellationToken ct)
    {
        await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
        long writtenSamples = 0;
        var chunk = new byte[SampleRate / 50 * 2 * 10]; // up to 200 ms per write
        while (!ct.IsCancellationRequested)
        {
            var due = (long)((clock.Elapsed.TotalSeconds - Volatile.Read(ref _timeBase)) * SampleRate) - writtenSamples;
            if (due > 0)
            {
                var bytes = (int)Math.Min(due * 2, chunk.Length);
                var filled = 0;
                var fillTo = Interlocked.Read(ref _audioFillTo);
                if (writtenSamples < fillTo)
                {
                    // Connect gap: silence up to the common point, without consuming live audio.
                    bytes = (int)Math.Min(bytes, (fillTo - writtenSamples) * 2);
                    Array.Clear(chunk, 0, bytes);
                    await pipe.WriteAsync(chunk.AsMemory(0, bytes & ~1), ct).ConfigureAwait(false);
                    writtenSamples += bytes / 2;
                    Interlocked.Exchange(ref _audioWritten, writtenSamples);
                    continue;
                }
                lock (_gate)
                {
                    while (filled < bytes && _audio.Count > 0)
                    {
                        var head = _audio.Peek();
                        var take = Math.Min(head.Length - _headOffset, bytes - filled);
                        Buffer.BlockCopy(head, _headOffset, chunk, filled, take);
                        filled += take;
                        _headOffset += take;
                        _audioBytes -= take;
                        if (_headOffset >= head.Length)
                        {
                            _audio.Dequeue();
                            _headOffset = 0;
                        }
                    }
                }
                Array.Clear(chunk, filled, bytes - filled); // silence when the phone sent nothing
                await pipe.WriteAsync(chunk.AsMemory(0, bytes & ~1), ct).ConfigureAwait(false);
                writtenSamples += bytes / 2;
                Interlocked.Exchange(ref _audioWritten, writtenSamples);
                continue;
            }
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    private static string RedactKey(string url)
    {
        var i = url.LastIndexOf('/');
        return i > 0 && i < url.Length - 4 ? url[..(i + 1)] + url[(i + 1)..(i + 3)] + "…" : url;
    }

    /// <summary>Turns ffmpeg's last error line into something a person can act on.</summary>
    private static string Explain(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "The stream stopped.";
        var e = error.ToLowerInvariant();
        if (e.Contains("connection refused") || e.Contains("error number -138") || e.Contains("timed out") || e.Contains("cannot open connection"))
            return "Can't reach the streaming server. Check your internet connection and server URL.";
        if (e.Contains("broken pipe") || e.Contains("connection reset") || e.Contains("end of file") || e.Contains("i/o error") || e.Contains("input/output error"))
            return "The server closed the connection. Check the stream key, or start a new stream in YouTube Studio.";
        if (e.Contains("unknown encoder") || e.Contains("no capable devices") || e.Contains("cannot load"))
            return "The video encoder isn't available on this PC.";
        return error.Length > 160 ? error[..160] + "…" : error;
    }
}
