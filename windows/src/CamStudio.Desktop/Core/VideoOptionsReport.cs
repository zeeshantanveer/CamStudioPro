using System.Text.Json.Nodes;
namespace CamStudio.Desktop.Core;

public sealed record VideoOptionsReport(int Resolution, int Background, bool Natural, string Acknowledgement, string Error, int Fps = 30)
{
    public static VideoOptionsReport? FromJson(JsonObject json)
    {
        static int Number(JsonObject j, string key, int fallback) => j[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : fallback;
        static string Text(JsonObject j, string key, int limit) => j[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s[..Math.Min(limit,s.Length)] : "";
        if (Number(json,"videoOptionsVersion",0) != 1) return null;
        return new(Number(json,"videoResolution",1080), Number(json,"videoBackground",0),
            json["videoNatural"] is JsonValue b && b.TryGetValue<bool>(out var enabled) && enabled,
            Text(json,"videoOptionsAck",80), Text(json,"videoOptionsError",180), Number(json,"videoFps",30));
    }
}
