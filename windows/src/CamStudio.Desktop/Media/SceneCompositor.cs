using System.Diagnostics;
using System.Runtime.InteropServices;
using CamStudio.Desktop.Platform;

namespace CamStudio.Desktop.Media;

/// <summary>
/// Builds the picture that goes to the preview, recordings and the live stream.
/// - Screen share off: the phone camera passes straight through, full size (no extra work or delay).
/// - Screen share on: the shared screen fills the frame (scaled to fit, letterboxed) and the camera sits
///   in a round bubble at the bottom right, with a white ring and a soft shadow.
/// The output always has the phone picture's size, so turning sharing on or off mid-recording or
/// mid-stream never changes the video size (recordings would otherwise have to stop).
/// </summary>
public sealed class SceneCompositor : IDisposable
{
    public enum BubbleSize { Small, Medium, Large }

    /// <summary>Receives every outgoing frame (recorder, live stream, preview).</summary>
    public Action<Nv12Frame>? Output;

    private readonly object _gate = new();
    private Nv12Frame? _camera;
    private long _cameraTicks;
    private ScreenCapture? _screen;
    private Thread? _thread;
    private volatile bool _running;
    private int _fps = 30;
    private int _lastW = 1920, _lastH = 1080;
    // The screen only changes when something on it changes: reuse the converted background until it does.
    private Nv12Frame? _background;
    private ScreenCapture.Frame? _backgroundSource;

    public BubbleSize Bubble { get; set; } = BubbleSize.Medium;
    public bool BubbleInPreview { get; set; }
    public bool BubbleInRecordings { get; set; }
    public bool BubbleInStreams { get; set; }
    public bool CameraEnabled { get; set; }
    public bool Sharing => _screen != null;
    public ScreenCapture? Screen => _screen;
    /// <summary>Milliseconds the last composed frame took (diagnostics).</summary>
    public double LastComposeMs { get; private set; }

    /// <summary>Frame rate for composed output (matches the phone's configured rate).</summary>
    public int Fps { get => _fps; set => _fps = Math.Clamp(value, 15, 60); }

    public void CameraFrame(Nv12Frame frame)
    {
        if (!CameraEnabled) return;
        lock (_gate)
        {
            if (!CameraEnabled) return;
            _camera = frame;
            _cameraTicks = Environment.TickCount64;
            if (!_running)
            {
                _lastW = frame.Width; _lastH = frame.Height;
                Output?.Invoke(frame);
            }
        }
    }

    /// <summary>Starts compositing with this screen source, or stops (null) and returns to camera only.</summary>
    public void SetScreen(ScreenCapture? screen)
    {
        var old = _screen;
        _screen = screen;
        if (screen != null && !_running)
        {
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Scene compositor", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }
        else if (screen == null && _running)
        {
            _running = false;
            _thread?.Join(1000);
            _thread = null;
        }
        if (old != null && !ReferenceEquals(old, screen))
            old.Dispose();
    }

    public void Dispose() => SetScreen(null);

    private void Loop()
    {
        using var timer = new PreciseTimer();
        var clock = Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            var screen = _screen;
            if (screen != null)
            {
                var t0 = Stopwatch.GetTimestamp();
                var frame = new Nv12Frame(_lastW, _lastH) { PtsUs = (long)(clock.Elapsed.TotalMilliseconds * 1000) };
                var shot = screen.Latest;
                if (_background == null || _background.Width != _lastW || _background.Height != _lastH || !ReferenceEquals(shot, _backgroundSource))
                {
                    _background ??= new Nv12Frame(_lastW, _lastH);
                    if (_background.Width != _lastW || _background.Height != _lastH) _background = new Nv12Frame(_lastW, _lastH);
                    DrawBackground(shot, _background);
                    _backgroundSource = shot;
                }
                Buffer.BlockCopy(_background.Data, 0, frame.Data, 0, frame.Data.Length);
                LastComposeMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                try { Output?.Invoke(frame); } catch (Exception e) { App.Log(e); }
            }
            n++;
            var wait = TimeSpan.FromSeconds((double)n / _fps) - clock.Elapsed;
            if (wait < -TimeSpan.FromSeconds(0.5))
                n = (long)(clock.Elapsed.TotalSeconds * _fps); // fell far behind (system busy): don't burst
            else if (wait > TimeSpan.Zero)
                timer.Wait(wait);
        }
    }

    // ====================================================================== drawing

    public void ClearCamera()
    {
        CameraEnabled = false;
        lock (_gate) { _camera = null; _cameraTicks = 0; }
    }

    /// <summary>One shared overlay variant; screen-only output is never modified in place.</summary>
    public sealed record OutputFrames(Nv12Frame Preview, Nv12Frame Recording, Nv12Frame Stream);

    public OutputFrames ForOutputs(Nv12Frame screenFrame, bool recording, bool streaming)
    {
        lock (_gate)
        {
            var camera = CameraEnabled && Environment.TickCount64 - _cameraTicks < 1500 ? _camera : null;
            return BuildOutputs(screenFrame, camera, Sharing, Bubble, BubbleInPreview,
                recording && BubbleInRecordings, streaming && BubbleInStreams);
        }
    }

    public static OutputFrames BuildOutputs(Nv12Frame source, Nv12Frame? camera, bool sharing,
        BubbleSize size, bool preview, bool recording, bool streaming)
    {
        if (!sharing || camera == null || !(preview || recording || streaming))
            return new(source,source,source);
        var overlay = new Nv12Frame(source.Width, source.Height) { PtsUs = source.PtsUs };
        Buffer.BlockCopy(source.Data,0,overlay.Data,0,source.Data.Length);
        DrawBubble(camera, overlay, size);
        return new(preview ? overlay : source, recording ? overlay : source, streaming ? overlay : source);
    }

    /// <summary>Draws one output frame. Public and static so it can be tested without capture hardware.</summary>
    public static void Compose(ScreenCapture.Frame? screen, Nv12Frame? camera, Nv12Frame dst, BubbleSize bubble)
    {
        DrawBackground(screen, dst);
        if (camera != null)
            DrawBubble(camera, dst, bubble);
    }

    /// <summary>Screen scaled to fit (bilinear, keeps text readable), letterboxed in near-black.</summary>
    private static unsafe void DrawBackground(ScreenCapture.Frame? screen, Nv12Frame dst)
    {
        int W = dst.Width, H = dst.Height;
        var y = dst.Data.AsSpan(0, W * H);
        var uv = dst.Data.AsSpan(W * H, W * H / 2);
        y.Fill(20);
        uv.Fill(128);
        if (screen == null)
            return;

        int sw = screen.Width, sh = screen.Height;
        var scale = Math.Min((double)W / sw, (double)H / sh);
        var dw = Math.Min(W, Math.Max(2, (int)Math.Round(sw * scale) & ~1));
        var dh = Math.Min(H, Math.Max(2, (int)Math.Round(sh * scale) & ~1));
        var x0 = ((W - dw) / 2) & ~1;
        var y0 = ((H - dh) / 2) & ~1;

        // Horizontal sample positions (16.16 fixed point), shared by all rows.
        var sx0 = new int[dw];
        var fx = new int[dw];
        for (var x = 0; x < dw; x++)
        {
            var s = Math.Max(0, (x + 0.5) * sw / dw - 0.5);
            var i = Math.Min((int)s, sw - 1);
            sx0[x] = i;
            fx[x] = (int)((s - i) * 256);
        }

        var src = screen.Bgra;
        var same = dw == sw && dh == sh; // screen already the output size: no scaling
        fixed (byte* pd = dst.Data)
        fixed (byte* ps = src)
        fixed (int* pxs = sx0)
        fixed (int* pfx = fx)
        {
            var d = (IntPtr)pd;
            var s0 = (IntPtr)ps;
            var xs = (IntPtr)pxs;
            var fxs = (IntPtr)pfx;
            Parallel.For(0, dh / 2, pair =>
            {
                var dP = (byte*)d;
                var sP = (byte*)s0;
                var xmap = (int*)xs;
                var fmap = (int*)fxs;
                for (var r = 0; r < 2; r++)
                {
                    var oy = pair * 2 + r;
                    var syf = Math.Max(0, (oy + 0.5) * sh / dh - 0.5);
                    var sy = Math.Min((int)syf, sh - 1);
                    var sy1 = Math.Min(sy + 1, sh - 1);
                    var fy = (int)((syf - sy) * 256);
                    var row0 = sP + (long)sy * sw * 4;
                    var row1 = sP + (long)sy1 * sw * 4;
                    var yRow = dP + (long)(y0 + oy) * W + x0;
                    var uvRow = r == 0 ? dP + (long)W * H + (long)((y0 + oy) / 2) * W + x0 : null;
                    for (var x = 0; x < dw; x++)
                    {
                        int B, G, R;
                        if (same)
                        {
                            var p = row0 + x * 4;
                            B = p[0]; G = p[1]; R = p[2];
                        }
                        else
                        {
                            var i0 = xmap[x] * 4;
                            var i1 = i0 + (xmap[x] < sw - 1 ? 4 : 0);
                            var f = fmap[x];
                            int t0 = row0[i0] + ((row0[i1] - row0[i0]) * f >> 8), t1 = row1[i0] + ((row1[i1] - row1[i0]) * f >> 8);
                            B = t0 + ((t1 - t0) * fy >> 8);
                            t0 = row0[i0 + 1] + ((row0[i1 + 1] - row0[i0 + 1]) * f >> 8); t1 = row1[i0 + 1] + ((row1[i1 + 1] - row1[i0 + 1]) * f >> 8);
                            G = t0 + ((t1 - t0) * fy >> 8);
                            t0 = row0[i0 + 2] + ((row0[i1 + 2] - row0[i0 + 2]) * f >> 8); t1 = row1[i0 + 2] + ((row1[i1 + 2] - row1[i0 + 2]) * f >> 8);
                            R = t0 + ((t1 - t0) * fy >> 8);
                        }
                        yRow[x] = (byte)(((66 * R + 129 * G + 25 * B + 128) >> 8) + 16);
                        if (uvRow != null && (x & 1) == 0)
                        {
                            // Chroma from the top-left sample of each 2×2 block (fast, sharp enough for UI/text).
                            var u = ((-38 * R - 74 * G + 112 * B + 128) >> 8) + 128;
                            var v = ((112 * R - 94 * G - 18 * B + 128) >> 8) + 128;
                            uvRow[x] = (byte)(u < 0 ? 0 : u > 255 ? 255 : u);
                            uvRow[x + 1] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                        }
                    }
                }
            });
        }
    }

    /// <summary>The camera in a circle at the bottom right: centre-cropped square, white ring, soft shadow.</summary>
    private static unsafe void DrawBubble(Nv12Frame cam, Nv12Frame dst, BubbleSize size)
    {
        int W = dst.Width, H = dst.Height;
        var shortSide = Math.Min(W, H);
        var diameter = shortSide * size switch { BubbleSize.Small => 0.24, BubbleSize.Large => 0.42, _ => 0.32 };
        var margin = shortSide * 0.045;
        var rOut = diameter / 2;
        var ring = Math.Max(2.0, diameter * 0.035);
        var rIn = rOut - ring;
        var shadow = Math.Max(3.0, diameter * 0.06);
        var cx = W - margin - rOut;
        var cy = H - margin - rOut;

        // Camera: centre square crop.
        int cw = cam.Width, ch = cam.Height;
        var side = Math.Min(cw, ch);
        var camX0 = (cw - side) / 2.0;
        var camY0 = (ch - side) / 2.0;
        var k = side / (2 * rIn); // source pixels per output pixel

        var bx0 = Math.Max(0, (int)(cx - rOut - shadow) & ~1);
        var by0 = Math.Max(0, (int)(cy - rOut - shadow) & ~1);
        var bx1 = Math.Min(W, (int)Math.Ceiling(cx + rOut + shadow + 2) & ~1);
        var by1 = Math.Min(H, (int)Math.Ceiling(cy + rOut + shadow + 2) & ~1);

        static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        fixed (byte* pd = dst.Data)
        fixed (byte* pc = cam.Data)
        {
            byte* dY0 = pd, dUV = pd + (long)W * H, cY0 = pc, cUV = pc + (long)cw * ch;

            // Luma, per pixel (rows in parallel).
            var dYp = (IntPtr)dY0; var cYp = (IntPtr)cY0;
            Parallel.For(by0, by1, y =>
            {
                byte* dY = (byte*)dYp, cY = (byte*)cYp;
                var row = dY + (long)y * W;
                for (var x = bx0; x < bx1; x++)
                {
                    double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                    var d = Math.Sqrt(dx * dx + dy * dy);
                    if (d > rOut + shadow) continue;
                    double v = row[x];
                    if (d > rOut - 0.5)
                    {
                        var s = 0.38 * Clamp01(1 - (d - rOut) / shadow);
                        v = v + (16 - v) * s * s * 2.2;                // soft shadow just outside the ring
                    }
                    v = v + (235 - v) * Clamp01(rOut - d + 0.5);       // white ring (anti-aliased outer edge)
                    var a = Clamp01(rIn - d + 0.5);                    // camera (anti-aliased inner edge)
                    if (a > 0)
                    {
                        // Bilinear luma sample from the camera crop.
                        var sx = camX0 + (dx + rIn) * k - 0.5;
                        var sy = camY0 + (dy + rIn) * k - 0.5;
                        var ix = Math.Clamp((int)sx, 0, cw - 2);
                        var iy = Math.Clamp((int)sy, 0, ch - 2);
                        var tx = Clamp01(sx - ix);
                        var ty = Clamp01(sy - iy);
                        var p = cY + (long)iy * cw + ix;
                        var top = p[0] + (p[1] - p[0]) * tx;
                        var bot = p[cw] + (p[cw + 1] - p[cw]) * tx;
                        var camY = top + (bot - top) * ty;
                        v = v + (camY - v) * a;
                    }
                    row[x] = (byte)Math.Clamp((int)(v + 0.5), 0, 255);
                }
            });

            // Chroma, per 2×2 block, using the block centre.
            for (var y = by0; y < by1; y += 2)
            {
                var row = dUV + (long)(y / 2) * W;
                for (var x = bx0; x < bx1; x += 2)
                {
                    double dx = x + 1 - cx, dy = y + 1 - cy;
                    var d = Math.Sqrt(dx * dx + dy * dy);
                    if (d > rOut + shadow) continue;
                    double u = row[x], vv = row[x + 1];
                    if (d > rOut - 1)
                    {
                        var s = 0.38 * Clamp01(1 - (d - rOut) / shadow);
                        var t = s * s * 2.2;
                        u += (128 - u) * t; vv += (128 - vv) * t;
                    }
                    var ringA = Clamp01(rOut - d + 1);
                    u += (128 - u) * ringA; vv += (128 - vv) * ringA;
                    var a = Clamp01(rIn - d + 1);
                    if (a > 0)
                    {
                        var sx = (int)(camX0 + (dx + rIn) * k);
                        var sy = (int)(camY0 + (dy + rIn) * k);
                        sx = Math.Clamp(sx, 0, cw - 2) & ~1;
                        sy = Math.Clamp(sy, 0, ch - 1) / 2;
                        var p = cUV + (long)sy * cw + sx;
                        u += (p[0] - u) * a; vv += (p[1] - vv) * a;
                    }
                    row[x] = (byte)Math.Clamp((int)(u + 0.5), 0, 255);
                    row[x + 1] = (byte)Math.Clamp((int)(vv + 0.5), 0, 255);
                }
            }
        }
    }

    /// <summary>High-resolution waitable timer: ~1 ms accuracy instead of the default 15.6 ms sleep.</summary>
    private sealed class PreciseTimer : IDisposable
    {
        private readonly IntPtr _h = CreateWaitableTimerExW(IntPtr.Zero, null, 0x00000002 /* HIGH_RESOLUTION */, 0x1F0003);

        public void Wait(TimeSpan span)
        {
            if (_h == IntPtr.Zero) { Thread.Sleep(span); return; }
            var due = -span.Ticks; // relative, 100 ns units
            if (SetWaitableTimer(_h, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                WaitForSingleObject(_h, 0xFFFFFFFF);
        }

        public void Dispose()
        {
            if (_h != IntPtr.Zero) CloseHandle(_h);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
        [DllImport("kernel32.dll")]
        private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);
        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
