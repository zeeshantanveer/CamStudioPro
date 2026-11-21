using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CamStudio.Desktop.Media;

namespace CamStudio.Desktop.Core;

public sealed record PhoneInfo(string DeviceId, string Name, string Model, string Transport, string Address);

/// <summary>Latest STATE from the phone (all optional).</summary>
public sealed record PhoneState(int? Battery, bool? Charging, double? TempC, int? WifiRssi, string? WifiBand,
                                bool Muted, bool Paused, int? SentFps, long? BitrateBps, int? RttMs, int? EncodeMs);

public sealed class SessionStats
{
    public double RecvFps;
    public long BitrateBps;
    public long DroppedFrames;
    public long TotalFrames;
    public int PipelineMs;
    public int Width;
    public int Height;
    public int ConfiguredFps;
}

/// <summary>
/// TCP 47801: accepts one phone at a time, decodes its video into the <see cref="FrameHub"/>
/// and its audio into the <see cref="AudioSink"/>. See docs/PROTOCOL.md.
/// </summary>
public sealed class SessionServer : IDisposable
{
    private readonly AppSettings _settings;
    private readonly FrameHub _hub;
    private readonly AudioSink _audio;
    private readonly Func<PhoneInfo, CancellationToken, Task<bool>> _approve;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Session? _active;
    private readonly object _gate = new();

    public SessionServer(AppSettings settings, FrameHub hub, AudioSink audio,
                         Func<PhoneInfo, CancellationToken, Task<bool>> approve)
    {
        _settings = settings;
        _hub = hub;
        _audio = audio;
        _approve = approve;
        Scene.Output = f =>
        {
            var recorder = Recorder;
            var streamer = Streamer;
            var outputs = Scene.ForOutputs(f, recorder != null, streamer != null);
            var recording = outputs.Recording;
            var streaming = outputs.Stream;
            if (ActiveStudioMic is { } mic)
            {
                var sameSource = ReferenceEquals(recording, streaming);
                if (recorder != null && !recorder.IsPaused && mic.Recording) recording = mic.ApplyOutput(recording);
                if (streamer != null && mic.Streaming)
                    streaming = sameSource && !ReferenceEquals(recording, outputs.Recording) ? recording : mic.ApplyOutput(streaming);
            }
            recorder?.PushVideo(recording);
            streamer?.PushVideo(streaming);
            _hub.PushLive(outputs.Preview);
        };
        EnhancementOptions = new(settings.VoiceEnhancement, settings.NoiseReduction,
            settings.AutomaticVoiceVolume, settings.VoicePreset);
    }

    public event Action<PhoneInfo>? Connected;
    public event Action<PhoneInfo, string>? Disconnected;
    public event Action? Updated;

    public VideoRecorder? Recorder;
    /// <summary>Live stream (YouTube/RTMP). Survives phone disconnects; it sends black + silence meanwhile.</summary>
    public LiveStreamer? Streamer;
    /// <summary>Camera only, or shared screen with the camera in a bubble. Feeds recorder, stream and preview.</summary>
    public SceneCompositor Scene { get; } = new();
    public volatile VoiceEnhancementOptions EnhancementOptions;
    public string VoiceStatus => _active?._voice.Status ?? "Ready";

    public string? Error { get; private set; }
    public PhoneInfo? Phone => _active?.Phone;
    public PhoneState? State => _active?.State;
    public SessionStats? Stats => _active?.Stats;
    public DateTime? ConnectedAt => _active?.ConnectedAt;
    public bool CameraEnabled => _active?.CameraEnabled == true;
    public bool CameraRequested => _active?.CameraRequested == true;
    internal volatile StudioMicrophone? StudioMic;
    internal StudioMicrophone? ActiveStudioMic
    {
        get
        {
            var mic = StudioMic;
            return mic != null && (!mic.FollowsAudio || _active?.HasLiveMicrophone == true) ? mic : null;
        }
    }
    public string? CameraError => _active?.CameraError;
    public void SetCameraEnabled(bool enabled)
    {
        var session = _active;
        if (session?.Phone != null) session.SetCameraEnabled(enabled);
    }
    public VideoOptionsReport? VideoOptions => _active?.VideoOptions;
    public void SetVideoOptions(JsonObject changes, string requestId) => _active?.SetVideoOptions(changes, requestId);
    public BeautyReport? Beauty => _active?.Beauty;
    public void SetBeauty(JsonObject changes, string requestId) => _active?.SetBeauty(changes, requestId);
    public void CompareBeauty(bool original) => _active?.CompareBeauty(original);

    public void Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, Protocol.SessionPort);
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
    }

    public void Disconnect() => _active?.Close("Disconnected from this PC");

    public void RequestKeyframe() => _active?.RequestKeyframe();

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                await Task.Delay(100).ConfigureAwait(false);
                continue;
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        client.NoDelay = true;
        client.ReceiveBufferSize = 2 * 1024 * 1024;
        var stream = client.GetStream();
        var header = new byte[5];
        var buffer = new byte[64 * 1024];

        try
        {
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            helloTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var hello = await Protocol.ReadAsync(stream, header, buffer, helloTimeout.Token).ConfigureAwait(false);
            if (hello.Type != Protocol.Hello)
            {
                client.Dispose();
                return;
            }

            var json = hello.Json();
            var version = json["v"]?.GetValue<int>() ?? 0;
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            var transport = json["transport"]?.GetValue<string>() ??
                            (remote != null && IPAddress.IsLoopback(remote) ? "usb" : "wifi");
            var phone = new PhoneInfo(
                json["deviceId"]?.GetValue<string>() ?? "",
                json["deviceName"]?.GetValue<string>() ?? "Android phone",
                json["model"]?.GetValue<string>() ?? "",
                transport,
                remote?.ToString() ?? "");

            async Task Reply(object ack) =>
                await stream.WriteAsync(Protocol.FrameJson(Protocol.HelloAck, ack), _cts.Token).ConfigureAwait(false);

            if (version != Protocol.Version || json["cameraControlVersion"]?.GetValue<int>() != 1)
            {
                await Reply(new { ok = false, reason = "version" }).ConfigureAwait(false);
                client.Dispose();
                return;
            }

            lock (_gate)
            {
                if (_active != null)
                {
                    _ = Reply(new { ok = false, reason = "busy" });
                    client.Dispose();
                    return;
                }
                _active = Session.Placeholder;
            }

            try
            {
                var pairKey = json["pairKey"]?.GetValue<string>();
                var trusted = phone.DeviceId.Length > 0 &&
                              (_settings.IsTrusted(phone.DeviceId) ||
                               (!string.IsNullOrEmpty(pairKey) && pairKey == _settings.PairKey));

                // Background auto-connect retries must never pop up the approval dialog.
                if (!trusted && json["auto"] is System.Text.Json.Nodes.JsonValue autoValue && autoValue.TryGetValue<bool>(out var auto) && auto)
                {
                    await Reply(new { ok = false, reason = "untrusted" }).ConfigureAwait(false);
                    client.Dispose();
                    lock (_gate) _active = null;
                    return;
                }

                if (!trusted)
                {
                    using var approval = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    approval.CancelAfter(TimeSpan.FromSeconds(55));
                    bool allowed;
                    try
                    {
                        allowed = await _approve(phone, approval.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        allowed = false;
                    }
                    if (!allowed)
                    {
                        await Reply(new { ok = false, reason = "denied" }).ConfigureAwait(false);
                        client.Dispose();
                        lock (_gate) _active = null;
                        return;
                    }
                }

                _settings.Trust(phone.DeviceId, phone.Name, phone.Model);
                await Reply(new { ok = true, pcId = _settings.PcId, pcName = Environment.MachineName, os = NetInfo.OsName(), cameraControlVersion = 1 }).ConfigureAwait(false);
            }
            catch
            {
                lock (_gate) _active = null;
                throw;
            }

            var session = new Session(this, client, phone);
            lock (_gate) _active = session;
            Connected?.Invoke(phone);

            var reason = await session.RunAsync(header, buffer).ConfigureAwait(false);

            lock (_gate) _active = null;
            Scene.ClearCamera();
            if (!Scene.Sharing) _hub.ClearLive();
            _audio.Stop();
            Disconnected?.Invoke(phone, reason);
        }
        catch
        {
            client.Dispose();
        }
    }

    private sealed class Session
    {
        public static readonly Session Placeholder = new();

        private readonly SessionServer _server;
        private readonly TcpClient _client = null!;
        private readonly NetworkStream _stream = null!;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private readonly Channel<(byte[] data, int length, long pts, byte flags, long rxTicks)> _video =
            Channel.CreateBounded<(byte[], int, long, byte, long)>(new BoundedChannelOptions(6) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly H264Decoder _decoder = new();
        internal readonly VoiceEnhancer _voice = new();
        private string _closeReason = "Phone disconnected";
        private long _lastRx = Environment.TickCount64;
        private volatile bool _needKeyframe = true;
        private long _lastKeyRequest;
        private long _cameraStartedTicks = long.MaxValue;
        private long _lastAudioTicks;
        private string _cameraRequestId = "";
        private volatile bool _cameraEnabled;
        private readonly object _captureGate = new();
        private int _configWidth, _configHeight;
        private volatile bool _reconfigure;

        // rolling stats
        private int _framesThisSecond;
        private long _bytesThisSecond;
        private long _dropped;
        private long _total;
        private double _pipelineAvg;

        public PhoneInfo Phone { get; } = null!;
        public PhoneState? State { get; private set; }
        public VideoOptionsReport? VideoOptions { get; private set; }
        public BeautyReport? Beauty { get; private set; }
        public SessionStats Stats { get; } = new();
        public DateTime ConnectedAt { get; } = DateTime.Now;
        public bool CameraEnabled { get => _cameraEnabled; private set => _cameraEnabled = value; }
        public bool CameraRequested { get; private set; }
        public bool HasLiveMicrophone => CameraEnabled && State?.Muted == false &&
            Interlocked.Read(ref _lastAudioTicks) is var ticks && ticks > 0 && Environment.TickCount64 - ticks < 1500;
        public string? CameraError { get; private set; }

        public void SetCameraEnabled(bool enabled)
        {
            string requestId;
            lock (_captureGate)
            {
                CameraRequested = enabled;
                requestId = _cameraRequestId = Guid.NewGuid().ToString("N");
                CameraError = null;
                if (!enabled) ClearCamera();
            }
            _ = SendAsync(Protocol.FrameJson(Protocol.Control, new { cmd = "camera", enabled, cameraControlVersion = 1, requestId }));
            _server.Updated?.Invoke();
        }

        private void ClearCamera()
        {
            lock (_captureGate)
            {
                CameraEnabled = false;
                Interlocked.Exchange(ref _lastAudioTicks, 0);
                Interlocked.Exchange(ref _cameraStartedTicks, long.MaxValue);
                _server.Scene.ClearCamera();
                if (!_server.Scene.Sharing) _server._hub.ClearLive();
                _server._audio.Stop();
                Stats.RecvFps = 0; Stats.BitrateBps = 0;
                _needKeyframe = true;
            }
        }

        private Session() => _server = null!;

        public Session(SessionServer server, TcpClient client, PhoneInfo phone)
        {
            _server = server;
            _client = client;
            _stream = client.GetStream();
            Phone = phone;
        }

        public void Close(string reason)
        {
            if (_client == null)
                return;
            _closeReason = reason;
            _ = SendAsync(Protocol.FrameJson(Protocol.Control, new { cmd = "bye" }));
            _cts.CancelAfter(200);
        }

        public void RequestKeyframe()
        {
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastKeyRequest) < 500)
                return;
            Interlocked.Exchange(ref _lastKeyRequest, now);
            _ = SendAsync(Protocol.FrameJson(Protocol.Control, new { cmd = "keyframe" }));
        }

        public void SetVideoOptions(JsonObject changes, string requestId) =>
            _ = SendAsync(Protocol.FrameJson(Protocol.Control, new { cmd = "videoOptions", changes, requestId }));

        public void SetBeauty(JsonObject changes, string requestId) =>
            _ = SendAsync(Protocol.FrameJson(Protocol.Control, new { cmd = "beauty", changes, requestId }));

        public void CompareBeauty(bool original) =>
            _ = SendAsync(Protocol.FrameJson(Protocol.Control, new { cmd = "beautyCompare", original }));

        private async Task SendAsync(byte[] bytes)
        {
            if (_stream == null)
                return;
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
            }
            catch
            {
                _cts.Cancel();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task<string> RunAsync(byte[] header, byte[] buffer)
        {
            var token = _cts.Token;
            using var link = CancellationTokenSource.CreateLinkedTokenSource(token, _server._cts.Token);
            var decodeTask = Task.Factory.StartNew(() => DecodeLoop(link.Token), TaskCreationOptions.LongRunning);
            var tickTask = Task.Run(() => TickLoopAsync(link.Token));

            try
            {
                while (!link.IsCancellationRequested)
                {
                    var msg = await Protocol.ReadAsync(_stream, header, buffer, link.Token).ConfigureAwait(false);
                    Interlocked.Exchange(ref _lastRx, Environment.TickCount64);
                    Handle(msg);
                }
            }
            catch (Exception) when (link.IsCancellationRequested || true)
            {
            }

            _cts.Cancel();
            _video.Writer.TryComplete();
            try { await Task.WhenAll(decodeTask, tickTask).ConfigureAwait(false); } catch { }
            _decoder.Dispose();
            _voice.Dispose();
            _server.Recorder?.Finish();
            _client.Dispose();
            return _closeReason;
        }

        private void Handle(Protocol.Message msg)
        {
            switch (msg.Type)
            {
                case Protocol.VideoConfig:
                {
                    var j = msg.Json();
                    var codec = j["codec"]?.GetValue<string>() ?? "h264";
                    if (codec != "h264")
                    {
                        Close($"Unsupported video codec {codec}");
                        return;
                    }
                    _configWidth = j["width"]?.GetValue<int>() ?? 1280;
                    _configHeight = j["height"]?.GetValue<int>() ?? 720;
                    Stats.ConfiguredFps = j["fps"]?.GetValue<int>() ?? 30;
                    _reconfigure = true;
                    _needKeyframe = true;
                    break;
                }

                case Protocol.VideoFrame:
                {
                    if (!CameraEnabled) return;
                    if (msg.Length < 9)
                        return;
                    var pts = (long)BinaryPrimitives.ReadUInt64BigEndian(msg.Span[..8]);
                    var flags = msg.Span[8];
                    var payload = msg.Span[9..];
                    Interlocked.Add(ref _bytesThisSecond, msg.Length);
                    Interlocked.Increment(ref _total);

                    var isKey = (flags & (Protocol.FlagKeyframe | Protocol.FlagCodecConfig)) != 0;
                    if (_needKeyframe && !isKey)
                    {
                        Interlocked.Increment(ref _dropped);
                        RequestKeyframe();
                        return;
                    }

                    var copy = payload.ToArray();
                    if (!_video.Writer.TryWrite((copy, copy.Length, pts, flags, Stopwatch.GetTimestamp())))
                    {
                        // decoder is behind: drop until the next keyframe instead of building latency
                        Interlocked.Increment(ref _dropped);
                        _needKeyframe = true;
                        RequestKeyframe();
                    }
                    else if (isKey)
                    {
                        _needKeyframe = false;
                    }
                    break;
                }

                case Protocol.AudioConfig:
                {
                    var j = msg.Json();
                    var sampleRate = j["sampleRate"]?.GetValue<int>() ?? 48000;
                    var channels = j["channels"]?.GetValue<int>() ?? 1;
                    _voice.Configure(sampleRate, channels);
                    _server._audio.Configure(sampleRate, channels);
                    break;
                }

                case Protocol.AudioFrame:
                    lock (_captureGate)
                    {
                    if (!CameraEnabled) return;
                    if (msg.Length > 8)
                    {
                        Interlocked.Exchange(ref _lastAudioTicks, Environment.TickCount64);
                        var enhanced = _voice.Process(msg.Span[8..], _server.EnhancementOptions);
                        if (enhanced.Length > 0)
                        {
                            _server.Recorder?.PushAudio(enhanced);
                            _server.Streamer?.PushAudio(enhanced);
                            _server._audio.Push(enhanced);
                        }
                    }
                    }
                    break;

                case Protocol.Ping:
                    if (msg.Length >= 8)
                        _ = SendAsync(Protocol.Frame(Protocol.Pong, msg.Span[..8]));
                    break;

                case Protocol.State:
                {
                    var j = msg.Json();
                    VideoOptions = VideoOptionsReport.FromJson(j) ?? VideoOptions;
                    Beauty = BeautyReport.FromJson(j) ?? Beauty;
                    State = new PhoneState(
                        Int(j, "battery"), Bool(j, "charging"), Dbl(j, "tempC"), Int(j, "wifiRssi"),
                        j["wifiBand"]?.GetValue<string>(), Bool(j, "muted") ?? false, Bool(j, "paused") ?? false,
                        Int(j, "sentFps"), Long(j, "bitrateBps"), Int(j, "rttMs"), Int(j, "encodeMs"));
                    _server._hub.SetPaused(State.Paused);
                    break;
                }

                case Protocol.Control:
                {
                    var control = msg.Json();
                    var cmd = control["cmd"]?.GetValue<string>();
                    switch (cmd)
                    {
                        case "cameraState":
                            lock (_captureGate)
                            {
                            var ack = control["requestId"]?.GetValue<string>();
                            if (!string.IsNullOrEmpty(ack) && ack != _cameraRequestId) break;
                            Interlocked.Exchange(ref _cameraStartedTicks, Stopwatch.GetTimestamp());
                            CameraEnabled = control["enabled"]?.GetValue<bool>() == true;
                            CameraRequested = CameraEnabled;
                            CameraError = control["error"]?.GetValue<string>();
                            _server.Scene.CameraEnabled = CameraEnabled;
                            if (!CameraEnabled) ClearCamera();
                            else RequestKeyframe();
                            }
                            _server.Updated?.Invoke();
                            break;
                        case "videoOptionsState":
                            VideoOptions = VideoOptionsReport.FromJson(control) ?? VideoOptions;
                            _server.Updated?.Invoke();
                            break;
                        case "beautyState":
                            Beauty = BeautyReport.FromJson(control) ?? Beauty;
                            _server.Updated?.Invoke();
                            break;
                        case "pause": _server._hub.SetPaused(true); break;
                        case "resume": _server._hub.SetPaused(false); RequestKeyframe(); break;
                        case "bye": _closeReason = "Phone stopped streaming"; _cts.Cancel(); break;
                    }
                    break;
                }
            }
        }

        private void DecodeLoop(CancellationToken ct)
        {
            var reader = _video.Reader;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult())
                        return;

                    while (reader.TryRead(out var item))
                    {
                        if (!CameraEnabled || item.rxTicks < Interlocked.Read(ref _cameraStartedTicks)) continue;
                        if (_reconfigure)
                        {
                            _reconfigure = false;
                            try
                            {
                                _decoder.Configure(_configWidth, _configHeight);
                                Stats.Width = _configWidth;
                                Stats.Height = _configHeight;
                            }
                            catch
                            {
                                _closeReason = "This PC could not start an H.264 decoder";
                                _cts.Cancel();
                                return;
                            }
                        }

                        try
                        {
                            var frames = _decoder.Decode(item.data.AsSpan(0, item.length), item.pts);
                            foreach (var f in frames)
                            {
                                if (!CameraEnabled || item.rxTicks < Interlocked.Read(ref _cameraStartedTicks)) continue;
                                f.PtsUs = item.pts;
                                _server.Scene.CameraFrame(f);
                                Interlocked.Increment(ref _framesThisSecond);
                                var ms = Stopwatch.GetElapsedTime(item.rxTicks).TotalMilliseconds;
                                _pipelineAvg = _pipelineAvg == 0 ? ms : _pipelineAvg * 0.9 + ms * 0.1;
                            }
                        }
                        catch
                        {
                            // corrupt input or decoder hiccup: restart decoder at the next keyframe
                            Interlocked.Increment(ref _dropped);
                            _reconfigure = true;
                            _needKeyframe = true;
                            RequestKeyframe();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task TickLoopAsync(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch { return; }

                var seconds = Math.Max(0.5, sw.Elapsed.TotalSeconds);
                sw.Restart();
                Stats.RecvFps = Interlocked.Exchange(ref _framesThisSecond, 0) / seconds;
                Stats.BitrateBps = (long)(Interlocked.Exchange(ref _bytesThisSecond, 0) * 8 / seconds);
                Stats.DroppedFrames = Interlocked.Read(ref _dropped);
                Stats.TotalFrames = Interlocked.Read(ref _total);
                Stats.PipelineMs = (int)Math.Round(_pipelineAvg);

                if (Environment.TickCount64 - Interlocked.Read(ref _lastRx) > 5000)
                {
                    _closeReason = "Connection lost";
                    _cts.Cancel();
                    return;
                }

                await SendAsync(Protocol.FrameJson(Protocol.Stats, new
                {
                    recvFps = (int)Math.Round(Stats.RecvFps),
                    bitrateBps = Stats.BitrateBps,
                    droppedFrames = Stats.DroppedFrames,
                    totalFrames = Stats.TotalFrames,
                    pipelineMs = Stats.PipelineMs,
                })).ConfigureAwait(false);

                _server.Updated?.Invoke();
            }
        }

        private static int? Int(JsonObject j, string k) => j[k] is JsonValue v && v.TryGetValue<double>(out var d) ? (int)d : null;
        private static long? Long(JsonObject j, string k) => j[k] is JsonValue v && v.TryGetValue<double>(out var d) ? (long)d : null;
        private static double? Dbl(JsonObject j, string k) => j[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
        private static bool? Bool(JsonObject j, string k) => j[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    }

    public void Dispose()
    {
        _active?.Close("PC app closed");
        _cts.Cancel();
        _listener?.Stop();
    }
}
