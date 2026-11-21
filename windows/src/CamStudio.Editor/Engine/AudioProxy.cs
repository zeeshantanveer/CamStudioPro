using System.IO;
using System.Security.Cryptography;
using System.Text;
using CamStudio.Editor.Model;

namespace CamStudio.Editor.Engine;

/// <summary>
/// Cleaned-audio previews. MediaElement can't run ffmpeg filters, so for a clip with clean-up turned on
/// the whole source audio is rendered once through the same filters (<see cref="VoiceChain.Build"/>, including
/// "Enhance voice")
/// into %TEMP%\CamStudioEditor\proxies and played instead of the original sound. Timing matches the
/// source, so the preview seeks it with the same source positions.
/// </summary>
public static class AudioProxy
{
    private static readonly Dictionary<string, string?> Ready = new();
    private static readonly HashSet<string> Rendering = new();
    private static readonly SemaphoreSlim Gate = new(1);

    /// <summary>Raised (on a worker thread) when a proxy finishes rendering (or fails).</summary>
    public static event Action? Changed;

    private static string Dir => Path.Combine(Path.GetTempPath(), "CamStudioEditor", "proxies");

    /// <summary>True while any proxy is being rendered.</summary>
    public static bool Busy { get { lock (Ready) return Rendering.Count > 0; } }

    /// <summary>
    /// Path of the cleaned audio for this clip's source and settings, or null if clean-up is off or the
    /// proxy is still rendering (rendering starts on the first call).
    /// </summary>
    public static string? Get(IAudioClip clip)
    {
        if (!clip.HasCleanup() || FFmpeg.FfmpegPath == null) return null;
        var settings = AudioCleanup.From(clip);
        var key = Key(clip.Path, settings.ToString());
        if (key == null) return null;
        lock (Ready)
        {
            if (Ready.TryGetValue(key, out var ready)) return ready;
            var file = Path.Combine(Dir, key + ".m4a");
            if (File.Exists(file))
            {
                Ready[key] = file;
                return file;
            }
            if (!Rendering.Add(key)) return null;
        }
        var source = clip.Path;
        _ = Task.Run(() => RenderAsync(key, source, settings));
        return null;
    }

    /// <summary>True if clean-up is on but its proxy isn't ready yet.</summary>
    public static bool IsPending(IAudioClip clip)
    {
        if (!clip.HasCleanup()) return false;
        var key = Key(clip.Path, AudioCleanup.From(clip).ToString());
        if (key == null) return false;
        lock (Ready) return Rendering.Contains(key);
    }

    private static string? Key(string path, string filter)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            // The engine choice is part of the key: a proxy made without the speech model isn't reused with it.
            var raw = $"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{filter}|{VoiceChain.UsesSpeechModel}|v3";
            return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)));
        }
        catch
        {
            return null;
        }
    }

    private static async Task RenderAsync(string key, string source, AudioCleanup settings)
    {
        string? result = null;
        double? floor = settings.Noise == NoiseLevel.Off ? null : await NoiseProfile.FloorAsync(source);
        var filter = VoiceChain.Build(settings, floor)!;
        await Gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Dir);
            var file = Path.Combine(Dir, key + ".m4a");
            var partial = Path.Combine(Dir, key + ".partial.m4a");
            var run = await FFmpeg.RunAsync(FFmpeg.FfmpegPath!, new[]
            {
                "-hide_banner", "-loglevel", "error", "-y", "-i", source, "-vn",
                "-af", filter + ",aresample=48000", "-c:a", "aac", "-b:a", "160k", partial,
            });
            if (run.ExitCode == 0 && File.Exists(partial))
            {
                File.Move(partial, file, true);
                result = file;
            }
            else
            {
                try { File.Delete(partial); } catch { }
            }
        }
        catch
        {
            result = null;
        }
        finally
        {
            Gate.Release();
        }
        lock (Ready)
        {
            Ready[key] = result;
            Rendering.Remove(key);
        }
        Changed?.Invoke();
    }
}
