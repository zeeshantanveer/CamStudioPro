using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace CamStudio.Desktop.Core;

public sealed class TrustedPhone
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTime LastUsed { get; set; }
}

/// <summary>Persisted settings in %APPDATA%\CamStudioPro\settings.json.</summary>
public sealed class AppSettings
{
    public string PcId { get; set; } = "";
    public string PairKey { get; set; } = "";
    public List<TrustedPhone> Trusted { get; set; } = new();
    /// <summary>WASAPI render device id for phone audio; empty = off.</summary>
    public string AudioOutputId { get; set; } = "";
    public bool AudioOutputChosen { get; set; }
    public bool StartMinimized { get; set; }
    public bool MirrorPreview { get; set; }
    public bool VoiceEnhancement { get; set; } = true;
    public string NoiseReduction { get; set; } = "Balanced";
    public bool AutomaticVoiceVolume { get; set; } = true;
    public string VoicePreset { get; set; } = "Natural";
    /// <summary>"System" (follow Windows), "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";
    /// <summary>RTMP(S) server for live streaming; YouTube's primary ingest by default.</summary>
    public string StreamUrl { get; set; } = YouTubeBroadcast.SecureServer;
    /// <summary>Stream key encrypted with Windows DPAPI for this user (base64). Use StreamKey to read/write.</summary>
    public string StreamKeyProtected { get; set; } = "";
    /// <summary>"Auto", "1080p", "720p" or "480p".</summary>
    public string StreamQuality { get; set; } = "Auto";
    /// <summary>Public watch link supplied by the broadcaster; never the RTMP ingest address.</summary>
    public string StreamViewerUrl { get; set; } = "";
    /// <summary>The quality step that last held during a stream (Auto starts there next time).</summary>
    public string LastStreamStep { get; set; } = "";
    /// <summary>Camera bubble size while sharing the screen: Small, Medium or Large.</summary>
    public string BubbleSize { get; set; } = "Medium";
    public bool BubbleInPreview { get; set; }
    public bool BubbleInRecordings { get; set; }
    public bool BubbleInStreams { get; set; }
    public List<int> BackgroundPlaylist { get; set; } = [2, 3];
    public int BackgroundIntervalSeconds { get; set; } = 30;
    public bool ShowVirtualMic { get; set; }
    public bool VirtualMicFollowsAudio { get; set; } = true;
    public bool VirtualMicInRecording { get; set; } = true;
    public bool VirtualMicInStream { get; set; } = true;
    public string VirtualMicText { get; set; } = "";
    public int VirtualMicStyle { get; set; }
    public int VirtualMicSize { get; set; } = 1;
    public int VirtualMicLabelFont { get; set; }
    public int VirtualMicLabelSize { get; set; } = 1;
    public bool VirtualMicLabelBold { get; set; } = true;
    public string VirtualMicLabelInk { get; set; } = "#FFFFFF";
    public string VirtualMicLabelLeft { get; set; } = "#18243C";
    public string VirtualMicLabelRight { get; set; } = "#40588B";

    [System.Text.Json.Serialization.JsonIgnore]
    public string StreamKey
    {
        get
        {
            if (string.IsNullOrEmpty(StreamKeyProtected)) return "";
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(StreamKeyProtected), null, DataProtectionScope.CurrentUser);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return "";
            }
        }
        set => StreamKeyProtected = string.IsNullOrEmpty(value) ? ""
            : Convert.ToBase64String(ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    private static readonly object Gate = new();
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CamStudioPro");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            settings = new AppSettings();
        }

        var dirty = false;
        if (string.IsNullOrEmpty(settings.PcId))
        {
            settings.PcId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            dirty = true;
        }
        if (string.IsNullOrEmpty(settings.PairKey))
        {
            settings.PairKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            dirty = true;
        }
        if (dirty)
            settings.Save();
        return settings;
    }

    public void Save()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, true);
        }
    }

    public bool IsTrusted(string deviceId) => Trusted.Any(t => t.DeviceId == deviceId);

    public void Trust(string deviceId, string name, string model)
    {
        lock (Gate)
        {
            var existing = Trusted.FirstOrDefault(t => t.DeviceId == deviceId);
            if (existing == null)
            {
                existing = new TrustedPhone { DeviceId = deviceId };
                Trusted.Add(existing);
            }
            existing.Name = name;
            existing.Model = model;
            existing.LastUsed = DateTime.Now;
        }
        Save();
    }

    public void Forget(string deviceId)
    {
        lock (Gate)
            Trusted.RemoveAll(t => t.DeviceId == deviceId);
        Save();
    }

    public void RotatePairKey()
    {
        PairKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        Save();
    }
}
