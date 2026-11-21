using System.Diagnostics;
using System.IO;

namespace CamStudio.Desktop.Platform;

public sealed record UsbDevice(string Serial, string State, string Model);

/// <summary>
/// USB mode: runs "adb reverse tcp:47801 tcp:47801" for every attached Android phone so the
/// phone app can reach this PC at 127.0.0.1. Needs adb (Android platform-tools) and USB
/// debugging enabled on the phone.
/// </summary>
public sealed class AdbBridge : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<string> _reversed = new();

    public string? AdbPath { get; private set; }
    public IReadOnlyList<UsbDevice> Devices { get; private set; } = Array.Empty<UsbDevice>();
    public event Action? Changed;

    public static string? FindAdb()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe"),
            Path.Combine(AppContext.BaseDirectory, "adb.exe"),
        };
        foreach (var env in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            var v = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(v))
                candidates.Add(Path.Combine(v, "platform-tools", "adb.exe"));
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim(), "adb.exe"));

        return candidates.FirstOrDefault(File.Exists);
    }

    public void Start()
    {
        AdbPath = FindAdb();
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                AdbPath ??= FindAdb();
                if (AdbPath != null)
                    await PollAsync(AdbPath).ConfigureAwait(false);
            }
            catch
            {
            }
            try { await Task.Delay(3000, _cts.Token).ConfigureAwait(false); } catch { return; }
        }
    }

    private async Task PollAsync(string adb)
    {
        var output = await RunAsync(adb, "devices -l").ConfigureAwait(false);
        var devices = new List<UsbDevice>();
        foreach (var line in output.Split('\n').Skip(1))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;
            var model = parts.FirstOrDefault(p => p.StartsWith("model:"))?[6..].Replace('_', ' ') ?? parts[0];
            devices.Add(new UsbDevice(parts[0], parts[1], model));
        }

        foreach (var d in devices.Where(d => d.State == "device"))
        {
            if (_reversed.Contains(d.Serial))
                continue;
            await RunAsync(adb, $"-s {d.Serial} reverse tcp:{Core.Protocol.SessionPort} tcp:{Core.Protocol.SessionPort}").ConfigureAwait(false);
            _reversed.Add(d.Serial);
        }
        _reversed.IntersectWith(devices.Where(d => d.State == "device").Select(d => d.Serial));

        var changed = devices.Count != Devices.Count || devices.Zip(Devices).Any(p => p.First != p.Second);
        Devices = devices;
        if (changed)
            Changed?.Invoke();
    }

    private static async Task<string> RunAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var exited = p.WaitForExitAsync();
        if (await Task.WhenAny(exited, Task.Delay(10000)).ConfigureAwait(false) != exited)
        {
            try { p.Kill(); } catch { }
            return "";
        }
        return await stdout.ConfigureAwait(false);
    }

    public void Dispose() => _cts.Cancel();
}
