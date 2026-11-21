using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using CamStudio.Desktop.Media;

// Exercise the real frame protocol without requiring administrator COM registration.
var hub = new FrameHub();
var type = typeof(VirtualCameraOutput);
using var output = (VirtualCameraOutput)Activator.CreateInstance(type, BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { hub }, null)!;
var listener = (TcpListener)type.GetField("_listener", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(output)!;
listener.Start();
var accept = (Task)type.GetMethod("AcceptAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(output, null)!;
using var client = new TcpClient();
await client.ConnectAsync(IPAddress.Loopback, VirtualCameraOutput.Port);
using var stream = client.GetStream();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
async Task<byte[]> Fetch(int w, int h)
{
    var request = new byte[8];
    BinaryPrimitives.WriteUInt32BigEndian(request, (uint)w);
    BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), (uint)h);
    // Deliberately fragment the request: TCP does not preserve message boundaries.
    foreach (var b in request) await stream.WriteAsync(new byte[] { b }, timeout.Token);
    var response = new byte[1 + w * h * 3 / 2];
    await stream.ReadExactlyAsync(response, timeout.Token);
    if (response[0] != 1) throw new Exception("Frame status failed");
    return response[1..];
}
foreach (var (w,h) in new[] {(640,360),(1280,720),(1920,1080)})
{
    hub.ClearLive();
    var black = await Fetch(w,h);
    if (black.Take(w*h).Any(b=>b!=16) || black.Skip(w*h).Any(b=>b!=128)) throw new Exception("Black fallback failed");
    var frame = new Nv12Frame(640,360);
    frame.Data.AsSpan(0,640*360).Fill(90);
    frame.Data.AsSpan(640*360).Fill(135);
    hub.PushLive(frame);
    var live = await Fetch(w,h);
    if (live.Take(w*h).Any(b=>b!=90) || live.Skip(w*h).Any(b=>b!=135)) throw new Exception("Live scaling failed");
    hub.SetPaused(true);
    if ((await Fetch(w,h))[0]!=16) throw new Exception("Pause leaked live video");
}
if (args.Length >= 2)
{
    async Task Native(bool black)
    {
        var live = new Nv12Frame(640, 360);
        live.Data.AsSpan(0, 640*360).Fill(90);
        live.Data.AsSpan(640*360).Fill(135);
        hub.SetPaused(false);
        if (black) hub.ClearLive(); else hub.PushLive(live);
        var start = new System.Diagnostics.ProcessStartInfo(args[0]) { UseShellExecute = false };
        var registeredCapture = args.Length > 2 && args[2] == "--registered-capture";
        var registeredLog = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CamStudioPro-frame-capture-" + Guid.NewGuid() + ".log");
        if (registeredCapture) {
            start.ArgumentList.Add("--registered"); start.ArgumentList.Add(registeredLog); start.ArgumentList.Add("--read");
        } else start.ArgumentList.Add(args[1]);
        if (black) start.ArgumentList.Add("--black");
        using var child = System.Diagnostics.Process.Start(start)!;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!child.HasExited)
        {
            if (!black) hub.PushLive(live);
            if (DateTime.UtcNow > deadline) { child.Kill(); throw new Exception("Native smoke timed out"); }
            await Task.Delay(100);
        }
        if (registeredCapture && System.IO.File.Exists(registeredLog)) {
            Console.WriteLine(await System.IO.File.ReadAllTextAsync(registeredLog));
            System.IO.File.Delete(registeredLog);
        }
        if (child.ExitCode != 0) throw new Exception($"Native smoke failed: {child.ExitCode}");
    }
    await Native(false);
    if (args.Length == 2) await Native(true);
}
output.Dispose();
await accept;
Console.WriteLine("PASS: all three camera resolutions, fragmented requests, live scaling, black fallback, pause and shutdown.");
