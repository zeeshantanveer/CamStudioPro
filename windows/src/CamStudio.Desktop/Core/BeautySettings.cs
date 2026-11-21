using System.Text.Json.Nodes;

namespace CamStudio.Desktop.Core;

public sealed record BeautyControl(string Key, string Label, int Minimum = 0, int Maximum = 100);

/// <summary>Protocol v1 controls. The phone owns/persists these settings; Windows sends partial edits.</summary>
public sealed record BeautySettings(bool Enabled, IReadOnlyDictionary<string, int> Values)
{
    public static IReadOnlyList<BeautyControl> Controls { get; } = new BeautyControl[]
    {
        new("faceSlim", "Face slimming"), new("jawSlim", "Jaw slimming"), new("chinLength", "Chin length", -100),
        new("noseWidth", "Nose narrowing", -100), new("noseBridge", "Nose bridge slimming", -100),
        new("noseAlar", "Nose alar narrowing", -100),
        new("noseLength", "Nose length", -100), new("noseRadix", "Nose root / radix lift", -100),
        new("noseTip", "Nose tip lift", -100), new("noseContour", "Nose contour shading"),
        new("smile", "Smile lift"), new("eyeSize", "Eye opening"),
        new("lipFullness", "Lip fullness", -100), new("skinSmooth", "Skin smoothing"), new("skinBrightness", "Skin brightness"),
    };

    public static BeautySettings FromJson(JsonObject json)
    {
        var enabled = json["enabled"] is JsonValue b && b.TryGetValue<bool>(out var e) && e;
        var values = Controls.ToDictionary(c => c.Key, c => json[c.Key] is JsonValue v && v.TryGetValue<int>(out var n)
            ? Math.Clamp(n, c.Minimum, c.Maximum) : 0);
        return new(enabled, values);
    }
}

public sealed record BeautyReport(BeautySettings Settings, string Tracking, string Acknowledgement, bool Original)
{
    public static BeautyReport? FromJson(JsonObject json)
    {
        if (json["beautyVersion"] is not JsonValue version || !version.TryGetValue<int>(out var v) || v != 1 ||
            json["beauty"] is not JsonObject settings) return null;
        static string Text(JsonNode? node, int max) => node is JsonValue value && value.TryGetValue<string>(out var s)
            ? s[..Math.Min(s.Length, max)] : "";
        var original = json["beautyOriginal"] is JsonValue flag && flag.TryGetValue<bool>(out var b) && b;
        return new(BeautySettings.FromJson(settings), Text(json["beautyTracking"], 160),
            Text(json["beautyAck"], 80), original);
    }
}
