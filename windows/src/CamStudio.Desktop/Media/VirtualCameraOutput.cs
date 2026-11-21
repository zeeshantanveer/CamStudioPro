using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using DirectN;
using CamStudio.Desktop.Platform;
using Microsoft.Win32;

namespace CamStudio.Desktop.Media;

/// <summary>Registers a session camera and serves the same composed frames as the preview.</summary>
public sealed class VirtualCameraOutput : IDisposable
{
    public const string SourceId = "{3c1f6a52-8e0b-4b7d-9f2a-6d4e8c1b7a35}";
    public const int Port = 47802;
    private readonly FrameHub _hub;
    private readonly CancellationTokenSource _stop = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, Port);
    private IMFVirtualCamera? _camera;
    private VirtualCameraOutput(FrameHub hub) => _hub = hub;

    public static async Task RegisterAsync()
    {
        if (PackageInfo.IsPackaged)
            throw new InvalidOperationException("The Store package registers the camera during installation. Repair or reinstall the approved package if its camera registration is missing.");
        var host = Path.Combine(AppContext.BaseDirectory, "VCam", "CamStudio.VCam.Native.dll");
        if (!File.Exists(host)) throw new FileNotFoundException("Virtual camera files are missing. Rebuild or reinstall CamStudio Pro.", host);
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "regsvr32.exe"))
        {
            UseShellExecute = true, Verb = "runas", Arguments = $"/s \"{host}\""
        }) ?? throw new InvalidOperationException("Could not start camera registration.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Camera registration failed ({process.ExitCode}).");
    }

    public static VirtualCameraOutput Start(FrameHub hub)
    {
        // MSIX registers its source in PackagedCom, not necessarily the classic CLSID
        // hive. Let Media Foundation resolve and validate the installed source.
        if (!PackageInfo.IsPackaged)
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\CLSID\{SourceId}\InprocServer32");
            if (key == null) throw new InvalidOperationException("Windows camera registration is missing.");
        }
        var output = new VirtualCameraOutput(hub);
        try
        {
            MFFunctions.MFStartup();
            output._listener.Start(4);
            // Software camera, session lifetime, current user. No administrator access needed to start.
            Functions.MFCreateVirtualCamera(0, 0, 0, "CamStudio Pro", SourceId, null!, 0, out var camera).ThrowOnError();
            output._camera = camera;
            camera.Start(null!).ThrowOnError();
            _ = output.AcceptAsync();
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = ServeAsync(client);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();
                var request = new byte[8];
                byte[] frame = [];
                byte[] ready = [1];
                while (!_stop.IsCancellationRequested)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await stream.ReadExactlyAsync(request, timeout.Token);
                    var width = BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(0, 4));
                    var height = BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(4, 4));
                    if (!((width == 1920 && height == 1080) || (width == 1280 && height == 720) || (width == 640 && height == 360))) return;
                    var length = checked((int)(width * height * 3 / 2));
                    if (frame.Length != length) frame = new byte[length];
                    Nv12Scaler.Fit(_hub.Current, frame, (int)width, (int)height);
                    await stream.WriteAsync(ready, timeout.Token);
                    await stream.WriteAsync(frame, timeout.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        if (_camera != null)
        {
            _camera.Shutdown();
            Marshal.ReleaseComObject(_camera);
            _camera = null;
        }
    }
}
