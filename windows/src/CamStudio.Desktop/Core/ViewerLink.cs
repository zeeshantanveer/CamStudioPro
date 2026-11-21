namespace CamStudio.Desktop.Core;

/// <summary>A shareable watch link is separate from the secret RTMP publishing address.</summary>
public static class ViewerLink
{
    public static bool TryNormalize(string? input, string? streamKey, out string link, out string error)
    {
        link = ""; error = "";
        var text = input?.Trim() ?? "";
        if (text.Length == 0) return true;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length > 0)
        {
            error = "Paste the viewer's http:// or https:// watch link from the streaming service.";
            return false;
        }
        if (!string.IsNullOrEmpty(streamKey) && (text.Contains(streamKey, StringComparison.Ordinal) ||
            Uri.UnescapeDataString(text).Contains(streamKey, StringComparison.Ordinal)))
        {
            error = "The viewer link must not contain your stream key.";
            return false;
        }
        if (uri.Host.Equals("studio.youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0] == "video" &&
                System.Text.RegularExpressions.Regex.IsMatch(parts[1], "^[A-Za-z0-9_-]{11}$"))
                link = "https://www.youtube.com/watch?v=" + parts[1];
            else
            {
                error = "Use the broadcast's Share link, not the YouTube Studio dashboard URL.";
                return false;
            }
        }
        else link = uri.AbsoluteUri;
        return true;
    }
}
