using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CamStudio.Desktop.Core;

public static class YouTubeBroadcast
{
    public const string SecureServer = "rtmps://a.rtmps.youtube.com:443/live2";

    private static readonly HttpClient WatchClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    })
    {
        Timeout = TimeSpan.FromSeconds(7),
        MaxResponseContentBufferSize = 4 * 1024 * 1024
    };

    public static string PreferSecureServer(string server)
    {
        // Upgrade the legacy built-in default. Custom servers and backup endpoints keep their settings.
        var value = server.Trim().TrimEnd('/');
        return value.Equals("rtmp://a.rtmp.youtube.com/live2", StringComparison.OrdinalIgnoreCase)
            || value.Equals("rtmp://a.rtmp.youtube.com:1935/live2", StringComparison.OrdinalIgnoreCase)
            ? SecureServer : server;
    }

    public static async Task<bool> HasEndedAsync(string viewerLink)
    {
        var id = VideoId(viewerLink);
        if (id == null) return false;
        try
        {
            // The public watch page only needs the video ID. The ingest key never goes here.
            var page = await WatchClient.GetStringAsync($"https://www.youtube.com/watch?v={id}")
                .ConfigureAwait(false);
            return HasEnded(page, id);
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) { return false; }
    }

    internal static string? VideoId(string viewerLink)
    {
        if (!Uri.TryCreate(viewerLink, UriKind.Absolute, out var link)) return null;
        var path = link.AbsolutePath.TrimEnd('/');
        string? id = null;
        if (link.Host == "youtu.be") id = path.TrimStart('/');
        if (link.Host is "youtube.com" or "www.youtube.com" or "m.youtube.com")
        {
            if (path.StartsWith("/live/", StringComparison.Ordinal)) id = path["/live/".Length..];
            if (path == "/watch")
                id = link.Query.TrimStart('?').Split('&')
                    .FirstOrDefault(parameter => parameter.StartsWith("v=", StringComparison.Ordinal))?[2..];
        }
        return id is { Length: 11 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? id : null;
    }

    internal static bool HasEnded(string page, string id)
    {
        const string marker = "ytInitialPlayerResponse =";
        int start = page.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return false;
        try
        {
            var json = Encoding.UTF8.GetBytes(page[(start + marker.Length)..]);
            var reader = new Utf8JsonReader(json);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            // Archived broadcasts include an end timestamp. Scheduled, live, private, and blocked
            // responses can omit it. Treat anything without positive evidence as inconclusive.
            return Child(Child(root, "videoDetails"), "videoId") is { ValueKind: JsonValueKind.String } videoId
                && videoId.ValueEquals(id)
                && Child(Child(Child(Child(root, "microformat"), "playerMicroformatRenderer"),
                        "liveBroadcastDetails"), "endTimestamp") is { ValueKind: JsonValueKind.String } ended
                && DateTimeOffset.TryParse(ended.GetString(), out _);
        }
        catch (JsonException) { return false; }
    }

    private static JsonElement Child(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value : default;
}
