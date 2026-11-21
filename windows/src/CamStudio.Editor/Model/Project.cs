using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CamStudio.Editor.Model;

/// <summary>An imported media file.</summary>
public sealed class MediaAsset
{
    public string Path { get; set; } = "";
    public double Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public bool HasVideo { get; set; }
    public bool HasAudio { get; set; }
    public bool IsImage { get; set; }

    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path);
    [JsonIgnore] public bool IsAudioOnly => HasAudio && !HasVideo && !IsImage;
}

/// <summary>Strength of the per-clip noise reduction (speech model arnndn, or spectral afftdn).</summary>
public enum NoiseLevel { Off, Light, Medium, Strong }

/// <summary>How strongly "Enhance voice" shapes speech (see ExportBuilder.VoiceStyles for the numbers).</summary>
public enum VoiceStyle { Natural, Podcast, Broadcast }

/// <summary>Final loudness of the exported mix (two-pass EBU R128), true peak −1 dBTP.</summary>
public enum LoudnessTarget { Off, YouTube, Podcast }

/// <summary>How far background music drops while someone speaks.</summary>
public enum DuckAmount { Light, Medium, Strong }

/// <summary>Audio clean-up settings shared by video-clip audio and music clips.</summary>
public interface IAudioClip
{
    Guid Id { get; }
    string Path { get; }
    double Volume { get; set; }
    bool Muted { get; set; }
    NoiseLevel NoiseReduction { get; set; }
    bool RemoveHum { get; set; }
    bool Normalize { get; set; }
    /// <summary>One-click voice chain: repair, EQ, de-ess, compression (plus the noise reduction above).</summary>
    bool Enhance { get; set; }
    VoiceStyle Style { get; set; }
}

public static class AudioClipExtensions
{
    public static bool HasCleanup(this IAudioClip c) => c.NoiseReduction != NoiseLevel.Off || c.RemoveHum || c.Normalize || c.Enhance;
}

/// <summary>Selection object for the A1 audio of a video clip (the audio part, edited on its own).</summary>
public sealed record AudioPart(VideoClip Clip);

/// <summary>A clip on the magnetic V1 track (with its linked A1 audio).</summary>
public sealed class VideoClip : IAudioClip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = "";
    public bool IsImage { get; set; }
    public bool HasAudio { get; set; }
    /// <summary>Length of the source file, for clamping trims. Images: effectively unlimited.</summary>
    public double SourceDuration { get; set; }
    public double In { get; set; }
    public double Out { get; set; }
    public double Speed { get; set; } = 1;
    public double Volume { get; set; } = 1;
    public bool Muted { get; set; }
    public double FadeIn { get; set; }
    public double FadeOut { get; set; }
    public double Brightness { get; set; }
    public double Contrast { get; set; } = 1;
    public double Saturation { get; set; } = 1;
    public NoiseLevel NoiseReduction { get; set; }
    public bool RemoveHum { get; set; }
    public bool Normalize { get; set; }
    public bool Enhance { get; set; }
    public VoiceStyle Style { get; set; }

    /// <summary>Position on the timeline; computed by <see cref="EditorProject.Layout"/>.</summary>
    [JsonIgnore] public double Start { get; set; }
    [JsonIgnore] public double Duration => Math.Max(0.01, (Out - In) / Speed);
    [JsonIgnore] public double End => Start + Duration;
    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path);
    [JsonIgnore] public bool HasColor => Math.Abs(Brightness) > 0.001 || Math.Abs(Contrast - 1) > 0.001 || Math.Abs(Saturation - 1) > 0.001;
    [JsonIgnore] public bool PlaysAudio => HasAudio && !IsImage && !Muted && Volume > 0.001;

    /// <summary>Source time shown at timeline time <paramref name="t"/>.</summary>
    public double SourceTimeAt(double t) => In + Math.Clamp(t - Start, 0, Duration) * Speed;
}

/// <summary>A free-positioned clip on the A2 music track.</summary>
public sealed class MusicClip : IAudioClip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = "";
    public double SourceDuration { get; set; }
    public double Start { get; set; }
    public double In { get; set; }
    public double Out { get; set; }
    public double Volume { get; set; } = 0.6;
    public double FadeIn { get; set; } = 1;
    public double FadeOut { get; set; } = 1;
    public bool Muted { get; set; }
    public NoiseLevel NoiseReduction { get; set; }
    public bool RemoveHum { get; set; }
    public bool Normalize { get; set; }
    public bool Enhance { get; set; }
    public VoiceStyle Style { get; set; }

    [JsonIgnore] public double Duration => Math.Max(0.01, Out - In);
    [JsonIgnore] public bool PlaysAudio => !Muted && Volume > 0.001;
    [JsonIgnore] public double End => Start + Duration;
    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path);
}

public enum TitlePosition { Top, Center, Bottom, LowerThird }
public enum TitleColor { White, Black, Accent }

/// <summary>A text overlay on the T1 track.</summary>
public sealed class TitleClip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "Title";
    public double Start { get; set; }
    public double Duration { get; set; } = 3;
    /// <summary>Font size in pixels at 1080 px output height; scaled for other sizes.</summary>
    public double FontSize { get; set; } = 64;
    public TitlePosition Position { get; set; } = TitlePosition.Bottom;
    public TitleColor Color { get; set; } = TitleColor.White;
    public bool Box { get; set; } = true;

    [JsonIgnore] public double End => Start + Duration;
}

public sealed class EditorProject
{
    /// <summary>2 = has the voice/loudness fields. Older files load with ducking off, as they exported before.</summary>
    public int FormatVersion { get; set; } = CurrentFormat;
    public const int CurrentFormat = 2;
    public string Name { get; set; } = "Untitled";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public double Fps { get; set; } = 30;
    public List<MediaAsset> Assets { get; set; } = new();
    public List<VideoClip> Video { get; set; } = new();
    public List<MusicClip> Music { get; set; } = new();
    public List<TitleClip> Titles { get; set; } = new();
    /// <summary>Final loudness of the export (two-pass loudnorm on the whole mix). New projects: YouTube.</summary>
    public LoudnessTarget LoudnessTarget { get; set; } = LoudnessTarget.YouTube;
    /// <summary>Lower the music automatically while the video's sound (the voice) is playing.</summary>
    public bool DuckMusic { get; set; } = true;
    public DuckAmount DuckAmount { get; set; } = DuckAmount.Medium;

    /// <summary>Exported length: the V1 track. Music and titles past the end are cut off.</summary>
    [JsonIgnore] public double Duration => Video.Count == 0 ? 0 : Video[^1].End;

    /// <summary>Length of an audio-only project (no video clips): up to the end of the last music clip.</summary>
    [JsonIgnore] public double MusicEnd => Music.Count == 0 ? 0 : Music.Max(m => m.End);

    /// <summary>What playback and export cover: the video track, or the music when there is no video.</summary>
    [JsonIgnore] public double PlayLength => Video.Count > 0 ? Duration : MusicEnd;

    /// <summary>True when there is anything to export (video, or audio-only music).</summary>
    [JsonIgnore] public bool CanExport => Video.Count > 0 || Music.Count > 0;

    /// <summary>Everything on the timeline, for scrolling.</summary>
    [JsonIgnore]
    public double Extent => Math.Max(Duration,
        Math.Max(Music.Count == 0 ? 0 : Music.Max(m => m.End), Titles.Count == 0 ? 0 : Titles.Max(t => t.End)));

    /// <summary>Recomputes V1 start times (V1 is magnetic: clips sit back to back).</summary>
    public void Layout()
    {
        double t = 0;
        foreach (var clip in Video)
        {
            clip.Start = t;
            t += clip.Duration;
        }
    }

    public VideoClip? VideoAt(double t)
    {
        foreach (var clip in Video)
            if (t >= clip.Start && t < clip.End)
                return clip;
        return null;
    }

    public MusicClip? MusicAt(double t) => Music.LastOrDefault(m => t >= m.Start && t < m.End);

    /// <summary>
    /// Splits <paramref name="clip"/> (video, music or title) at timeline time <paramref name="t"/>.
    /// Returns the new right-hand part, or null when <paramref name="t"/> isn't inside the clip.
    /// </summary>
    public object? SplitAt(object clip, double t, double margin = 0.05)
    {
        Layout();
        switch (clip)
        {
            case VideoClip v when t > v.Start + margin && t < v.End - margin:
            {
                var src = v.SourceTimeAt(t);
                var copy = Clone(v);
                copy.Id = Guid.NewGuid();
                copy.In = v.IsImage ? 0 : src;
                copy.Out = v.IsImage ? (v.Out - v.In) - (t - v.Start) : v.Out;
                if (v.IsImage) v.Out = t - v.Start; else v.Out = src;
                copy.FadeIn = 0;
                v.FadeOut = 0;
                Video.Insert(Video.IndexOf(v) + 1, copy);
                Layout();
                return copy;
            }
            case MusicClip m when t > m.Start + margin && t < m.End - margin:
            {
                var src = m.In + (t - m.Start);
                var copy = Clone(m);
                copy.Id = Guid.NewGuid();
                copy.Start = t;
                copy.In = src;
                copy.FadeIn = 0;
                m.Out = src;
                m.FadeOut = 0;
                Music.Add(copy);
                return copy;
            }
            case TitleClip ti when t > ti.Start + margin && t < ti.End - margin:
            {
                var copy = Clone(ti);
                copy.Id = Guid.NewGuid();
                copy.Start = t;
                copy.Duration = ti.End - t;
                ti.Duration = t - ti.Start;
                Titles.Add(copy);
                return copy;
            }
        }
        return null;
    }

    /// <summary>
    /// Removes source-time ranges (e.g. long pauses) from a video or music clip by splitting it into the
    /// parts that remain. V1 ripples by itself; the music parts are pulled together so no gaps remain.
    /// Fades stay on the first and last part. Returns the number of ranges removed and timeline seconds saved.
    /// </summary>
    public (int Count, double Seconds) RemoveRanges(object clip, IReadOnlyList<(double Start, double End)> cuts)
    {
        double clipIn, clipOut, speed;
        switch (clip)
        {
            case VideoClip v when !v.IsImage: clipIn = v.In; clipOut = v.Out; speed = v.Speed; break;
            case MusicClip m: clipIn = m.In; clipOut = m.Out; speed = 1; break;
            default: return (0, 0);
        }
        var ranges = cuts.Select(c => (Start: Math.Max(clipIn, c.Start), End: Math.Min(clipOut, c.End)))
            .Where(c => c.End - c.Start > 0.05).OrderBy(c => c.Start).ToList();
        if (ranges.Count == 0) return (0, 0);

        // The kept parts, in source time.
        var keep = new List<(double In, double Out)>();
        var cursor = clipIn;
        foreach (var (start, end) in ranges)
        {
            if (start - cursor > 0.05) keep.Add((cursor, start));
            cursor = Math.Max(cursor, end);
        }
        if (clipOut - cursor > 0.05) keep.Add((cursor, clipOut));
        if (keep.Count == 0) return (0, 0); // never delete the whole clip this way
        var removed = ((clipOut - clipIn) - keep.Sum(k => k.Out - k.In)) / speed;

        switch (clip)
        {
            case VideoClip v:
            {
                var index = Video.IndexOf(v);
                var parts = keep.Select((k, i) =>
                {
                    var c = i == 0 ? v : Clone(v);
                    if (i > 0) c.Id = Guid.NewGuid();
                    c.In = k.In;
                    c.Out = k.Out;
                    c.FadeIn = i == 0 ? v.FadeIn : 0;
                    return c;
                }).ToList();
                var fadeOut = v.FadeOut;
                foreach (var c in parts) c.FadeOut = 0;
                parts[^1].FadeOut = fadeOut;
                Video.RemoveAt(index);
                Video.InsertRange(index, parts);
                Layout();
                break;
            }
            case MusicClip m:
            {
                var start = m.Start;
                var fadeOut = m.FadeOut;
                var parts = new List<MusicClip>();
                var t = start;
                for (var i = 0; i < keep.Count; i++)
                {
                    var c = i == 0 ? m : Clone(m);
                    if (i > 0) c.Id = Guid.NewGuid();
                    c.In = keep[i].In;
                    c.Out = keep[i].Out;
                    c.Start = t;
                    c.FadeIn = i == 0 ? m.FadeIn : 0;
                    c.FadeOut = 0;
                    t += c.Duration;
                    parts.Add(c);
                }
                parts[^1].FadeOut = fadeOut;
                // Later music clips move earlier by the time removed, so the track stays continuous.
                foreach (var other in Music.Where(o => !ReferenceEquals(o, m) && o.Start >= start + 0.001))
                    other.Start = Math.Max(0, other.Start - removed);
                Music.AddRange(parts.Skip(1));
                break;
            }
        }
        return (ranges.Count, removed);
    }

    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;

    public MediaAsset? Asset(string path) => Assets.FirstOrDefault(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));

    public VideoClip CreateVideoClip(MediaAsset asset) => new()
    {
        Path = asset.Path,
        IsImage = asset.IsImage,
        HasAudio = asset.HasAudio && !asset.IsImage,
        SourceDuration = asset.IsImage ? 3600 : asset.Duration,
        In = 0,
        Out = asset.IsImage ? 5 : Math.Max(0.1, asset.Duration),
    };

    public MusicClip CreateMusicClip(MediaAsset asset, double start) => new()
    {
        Path = asset.Path,
        SourceDuration = asset.Duration,
        Start = Math.Max(0, start),
        In = 0,
        Out = Math.Max(0.1, asset.Duration),
    };

    // ---------- serialization ----------

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static EditorProject FromJson(string json)
    {
        var project = JsonSerializer.Deserialize<EditorProject>(json, Json) ?? new EditorProject();
        if (project.FormatVersion < 2)
        {
            // Projects from before these settings existed: keep exporting exactly as they did
            // (the loudness meter still tells the user if the result is too quiet).
            project.DuckMusic = false;
            project.LoudnessTarget = LoudnessTarget.Off;
            project.FormatVersion = CurrentFormat;
        }
        project.Layout();
        return project;
    }

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson());
        File.Move(tmp, path, true);
    }

    public static EditorProject Load(string path) => FromJson(File.ReadAllText(path));
}
