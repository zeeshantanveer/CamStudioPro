using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace CamStudio.Desktop.Platform;

/// <summary>
/// Captures a screen or window with Windows.Graphics.Capture (the same API the Snipping Tool and Teams
/// use). Windows draws its yellow capture border around what is shared, and the user picks the source in
/// the system picker. Frames are copied to the CPU as BGRA for compositing; only the newest is kept.
/// </summary>
public sealed class ScreenCapture : IDisposable
{
    public sealed class Frame
    {
        public required byte[] Bgra { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required long Ticks { get; init; }
    }

    private readonly GraphicsCaptureItem _item;
    private readonly IDirect3DDevice _device;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private Windows.Graphics.SizeInt32 _size;
    private int _busy;
    private volatile Frame? _latest;
    private bool _disposed;

    /// <summary>Name of the shared window or screen, as Windows reports it.</summary>
    public string Name => _item.DisplayName;
    public Frame? Latest => _latest;
    /// <summary>Raised when the shared window closes or the user stops sharing from Windows.</summary>
    public event Action? Ended;
    /// <summary>Frames copied per second (for diagnostics).</summary>
    public double Fps { get; private set; }

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    private ScreenCapture(GraphicsCaptureItem item)
    {
        _item = item;
        _device = Direct3D.CreateDevice();
        _size = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(item);
        try { _session.IsCursorCaptureEnabled = true; } catch { }
        _item.Closed += (_, _) => Ended?.Invoke();
        _session.StartCapture();
    }

    /// <summary>Shows the Windows picker (screens and windows). Null if the user cancels.</summary>
    public static async Task<ScreenCapture?> PickAsync(IntPtr ownerHwnd)
    {
        var picker = new GraphicsCapturePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerHwnd);
        var item = await picker.PickSingleItemAsync();
        return item == null ? null : new ScreenCapture(item);
    }

    /// <summary>Captures a whole monitor without the picker (tests, "primary screen" shortcut).</summary>
    public static ScreenCapture ForMonitor(IntPtr hmonitor) => new(Interop.CreateForMonitor(hmonitor));

    public static ScreenCapture ForPrimaryMonitor() => ForMonitor(MonitorFromPoint(new POINT(), 1 /* MONITOR_DEFAULTTOPRIMARY */));

    private long _countStart = Environment.TickCount64;
    private int _count;

    private async void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        var frame = sender.TryGetNextFrame();
        if (frame == null)
            return;
        try
        {
            var size = frame.ContentSize;
            if (size.Width != _size.Width || size.Height != _size.Height)
            {
                // The shared window was resized: recreate the pool for the new size.
                _size = size;
                sender.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                return;
            }
            // Keep up with the display: if the previous copy is still running, skip this frame.
            if (Interlocked.Exchange(ref _busy, 1) == 1)
                return;
            try
            {
                using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore);
                var w = bitmap.PixelWidth & ~1;
                var h = bitmap.PixelHeight & ~1;
                if (w < 2 || h < 2) return;
                var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyToBuffer(bytes.AsBuffer());
                if (w != bitmap.PixelWidth || h != bitmap.PixelHeight)
                    bytes = Crop(bytes, bitmap.PixelWidth, w, h);
                _latest = new Frame { Bgra = bytes, Width = w, Height = h, Ticks = Environment.TickCount64 };
                _count++;
                var now = Environment.TickCount64;
                if (now - _countStart >= 1000)
                {
                    Fps = _count * 1000.0 / (now - _countStart);
                    _count = 0;
                    _countStart = now;
                }
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        }
        catch (Exception e)
        {
            if (!_disposed) App.Log(e);
        }
        finally
        {
            frame.Dispose();
        }
    }

    private static byte[] Crop(byte[] src, int srcWidth, int w, int h)
    {
        var dst = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            Buffer.BlockCopy(src, y * srcWidth * 4, dst, y * w * 4, w * 4);
        return dst;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _session.Dispose(); } catch { }
        try { _pool.Dispose(); } catch { }
        try { (_device as IDisposable)?.Dispose(); } catch { }
    }

    // ------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    /// <summary>Direct3D 11 device wrapped as the WinRT IDirect3DDevice the capture API needs.</summary>
    private static class Direct3D
    {
        private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
            IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

        [DllImport("d3d11.dll")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        public static IDirect3DDevice CreateDevice()
        {
            const int Hardware = 1, Warp = 5;
            const uint BgraSupport = 0x20;
            var hr = D3D11CreateDevice(IntPtr.Zero, Hardware, IntPtr.Zero, BgraSupport, IntPtr.Zero, 0, 7, out var device, out _, out var context);
            if (hr < 0)
                hr = D3D11CreateDevice(IntPtr.Zero, Warp, IntPtr.Zero, BgraSupport, IntPtr.Zero, 0, 7, out device, out _, out context);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in IID_IDXGIDevice, out var dxgi));
                try
                {
                    Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                    try
                    {
                        return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                    }
                    finally
                    {
                        Marshal.Release(inspectable);
                    }
                }
                finally
                {
                    Marshal.Release(dxgi);
                }
            }
            finally
            {
                if (context != IntPtr.Zero) Marshal.Release(context);
                Marshal.Release(device);
            }
        }
    }

    /// <summary>IGraphicsCaptureItemInterop: capture items for a monitor or window without the picker.</summary>
    private static unsafe class Interop
    {
        private static readonly Guid IID_Interop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        private static readonly Guid IID_GraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

        public static GraphicsCaptureItem CreateForMonitor(IntPtr hmonitor)
        {
            using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in IID_Interop, out var interop));
            try
            {
                // vtable: IUnknown (3), CreateForWindow (3), CreateForMonitor (4)
                var vtbl = *(IntPtr**)interop;
                var createForMonitor = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[4];
                IntPtr item;
                var iid = IID_GraphicsCaptureItem;
                Marshal.ThrowExceptionForHR(createForMonitor(interop, hmonitor, &iid, &item));
                try
                {
                    return GraphicsCaptureItem.FromAbi(item);
                }
                finally
                {
                    Marshal.Release(item);
                }
            }
            finally
            {
                Marshal.Release(interop);
            }
        }
    }
}
