namespace CamStudio.Desktop.Media;

/// <summary>Holds the phone's newest decoded frame for the on-screen preview and the recorder.</summary>
public sealed class FrameHub
{
    private readonly object _lock = new();
    private Nv12Frame? _live;
    private long _liveTicks;
    private bool _paused;
    private long _version;

    public long Version => Interlocked.Read(ref _version);
    /// <summary>While sharing the screen, keep showing frames even if the phone paused its camera.</summary>
    public bool IgnorePause { get; set; }

    public void PushLive(Nv12Frame frame)
    {
        lock (_lock)
        {
            _live = frame;
            _liveTicks = Environment.TickCount64;
            _version++;
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_lock)
        {
            _paused = paused;
            _version++;
        }
    }

    public void ClearLive()
    {
        lock (_lock)
        {
            _live = null;
            _paused = false;
            _version++;
        }
    }

    /// <summary>The live frame while it is fresh and not paused, otherwise null.</summary>
    public Nv12Frame? Current
    {
        get
        {
            lock (_lock)
            {
                if (_live != null && (!_paused || IgnorePause) && Environment.TickCount64 - _liveTicks < 3000)
                    return _live;
                return null;
            }
        }
    }

    public bool IsShowingLive
    {
        get
        {
            lock (_lock)
                return _live != null && (!_paused || IgnorePause) && Environment.TickCount64 - _liveTicks < 3000;
        }
    }

}

public static unsafe class Nv12Scaler
{
    /// <summary>Nearest-neighbour fit into width×height with black bars (live stream output). Null source = black.</summary>
    public static void Fit(Nv12Frame? src, byte[] dst, int width, int height)
    {
        dst.AsSpan(0, width * height).Fill(16);
        dst.AsSpan(width * height, width * height / 2).Fill(128);
        if (src == null)
            return;
        if (src.Width == width && src.Height == height && src.Data.Length >= dst.Length)
        {
            Buffer.BlockCopy(src.Data, 0, dst, 0, dst.Length); // same size (e.g. 4K phone → 4K stream): plain copy
            return;
        }
        var sw = src.Width;
        var sh = src.Height;
        var scale = Math.Min((double)width / sw, (double)height / sh);
        var dw = Math.Min(width, Math.Max(2, (int)Math.Round(sw * scale) & ~1));
        var dh = Math.Min(height, Math.Max(2, (int)Math.Round(sh * scale) & ~1));
        var x0 = ((width - dw) / 2) & ~1;
        var y0 = ((height - dh) / 2) & ~1;

        var xmap = new int[dw];
        for (var x = 0; x < dw; x++)
            xmap[x] = Math.Clamp((int)((x + 0.5) * sw / dw), 0, sw - 1);

        fixed (byte* s = src.Data)
        fixed (byte* d = dst)
        {
            for (var y = 0; y < dh; y++)
            {
                var sy = Math.Min(sh - 1, (int)((y + 0.5) * sh / dh));
                var srow = s + sy * sw;
                var drow = d + (y0 + y) * width + x0;
                for (var x = 0; x < dw; x++)
                    drow[x] = srow[xmap[x]];
            }
            var suv = s + sw * sh;
            var duv = d + width * height;
            for (var y = 0; y < dh / 2; y++)
            {
                var sy = Math.Min(sh / 2 - 1, (int)((2 * y + 1.0) * sh / dh) / 2);
                var srow = suv + sy * sw;
                var drow = duv + (y0 / 2 + y) * width + x0;
                for (var x = 0; x < dw / 2; x++)
                {
                    var sx = xmap[2 * x] & ~1;
                    drow[2 * x] = srow[sx];
                    drow[2 * x + 1] = srow[sx + 1];
                }
            }
        }
    }

    /// <summary>NV12 → BGRA32 at a reduced size for the on-screen preview (BT.601 limited range).</summary>
    public static void ToBgra(Nv12Frame src, byte[] bgra, int width, int height, bool mirror)
    {
        var sw = src.Width;
        var sh = src.Height;
        fixed (byte* s = src.Data)
        fixed (byte* d = bgra)
        {
            var uv = s + sw * sh;
            for (var y = 0; y < height; y++)
            {
                var sy = Math.Min(sh - 1, y * sh / height);
                var yrow = s + sy * sw;
                var uvrow = uv + (sy / 2) * sw;
                var drow = d + y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    var sx = Math.Min(sw - 1, x * sw / width);
                    if (mirror)
                        sx = sw - 1 - sx;
                    var c = yrow[sx] - 16;
                    var u = uvrow[sx & ~1] - 128;
                    var v = uvrow[(sx & ~1) + 1] - 128;
                    var r = (298 * c + 409 * v + 128) >> 8;
                    var g = (298 * c - 100 * u - 208 * v + 128) >> 8;
                    var b = (298 * c + 516 * u + 128) >> 8;
                    var p = drow + x * 4;
                    p[0] = (byte)Math.Clamp(b, 0, 255);
                    p[1] = (byte)Math.Clamp(g, 0, 255);
                    p[2] = (byte)Math.Clamp(r, 0, 255);
                    p[3] = 255;
                }
            }
        }
    }

}
