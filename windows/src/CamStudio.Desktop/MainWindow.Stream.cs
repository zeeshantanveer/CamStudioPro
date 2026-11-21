using System.Windows;
using System.Windows.Controls;
using CamStudio.Desktop.Media;
using CamStudio.Desktop.Core;
using FFmpegTools = CamStudio.Editor.Engine.FFmpeg;

namespace CamStudio.Desktop;

/// <summary>
/// Live streaming to YouTube (or any RTMP/RTMPS server). The user pastes the stream key from
/// YouTube Studio › Go live › Stream once; it's stored encrypted for their Windows account.
/// </summary>
public partial class MainWindow
{
    private const string YouTubeStudioLive = "https://studio.youtube.com/channel/UC/livestreaming";
    private LiveStreamer? _streamer;
    private bool _streamBusy;
    private string _activeViewerUrl = "";

    private bool StreamConfigured => !string.IsNullOrWhiteSpace(_settings.StreamKey) && !string.IsNullOrWhiteSpace(_settings.StreamUrl);

    private void InitStream()
    {
        StreamSetupPanel.Visibility = Visibility.Collapsed;
        UpdateStreamControls();
    }

    // ---------- setup ----------

    private void StreamEdit_Click(object sender, RoutedEventArgs e)
    {
        StreamSetupErrorText.Text = "";
        StreamUrlBox.IsEnabled = _streamer == null;
        StreamKeyBox.IsEnabled = _streamer == null;
        StreamQualityCombo.IsEnabled = _streamer == null;
        StreamViewerUrlBox.Text = _settings.StreamViewerUrl;
        StreamUrlBox.Text = YouTubeBroadcast.PreferSecureServer(_settings.StreamUrl);
        StreamKeyBox.Password = _settings.StreamKey;
        StreamQualityCombo.SelectedItem = StreamQualityCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == _settings.StreamQuality) ?? StreamQualityCombo.Items[0];
        StreamSetupPanel.Visibility = Visibility.Visible;
        StreamEditButton.Visibility = Visibility.Collapsed;
        StreamKeyBox.Focus();
    }

    private void StreamSave_Click(object sender, RoutedEventArgs e)
    {
        var url = StreamUrlBox.Text.Trim();
        if (!url.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase))
        {
            StreamSetupErrorText.Text = "The server URL starts with rtmp:// or rtmps://";
            StreamUrlBox.Focus();
            return;
        }
        if (!ViewerLink.TryNormalize(StreamViewerUrlBox.Text, StreamKeyBox.Password.Trim(), out var viewerLink, out var viewerError))
        {
            StreamSetupErrorText.Text = viewerError;
            StreamViewerUrlBox.Focus();
            return;
        }
        _settings.StreamViewerUrl = viewerLink;
        if (_streamer != null) _activeViewerUrl = viewerLink;
        _settings.StreamUrl = url;
        _settings.StreamKey = StreamKeyBox.Password.Trim();
        _settings.StreamQuality = (StreamQualityCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Auto";
        _settings.Save();
        StreamKeyBox.Clear();
        StreamSetupPanel.Visibility = Visibility.Collapsed;
        StreamEditButton.Visibility = Visibility.Visible;
        UpdateStreamControls();
    }

    private void StreamCancel_Click(object sender, RoutedEventArgs e)
    {
        StreamKeyBox.Clear();
        StreamSetupPanel.Visibility = Visibility.Collapsed;
        StreamEditButton.Visibility = Visibility.Visible;
        UpdateStreamControls();
    }

    private void CopyStreamViewer_Click(object sender, RoutedEventArgs e)
    {
        if (_streamer == null || _activeViewerUrl.Length == 0) return;
        try { Clipboard.SetText(_activeViewerUrl); StreamViewerHint.Text = "Viewer link copied."; }
        catch (System.Runtime.InteropServices.ExternalException) { StreamViewerHint.Text = "Clipboard is busy. Select the link and copy it manually."; }
    }

    private void OpenStreamViewer_Click(object sender, RoutedEventArgs e)
    {
        if (_streamer != null && _activeViewerUrl.Length > 0) OpenUrl(_activeViewerUrl);
    }

    private void OpenYouTubeStudio_Click(object sender, RoutedEventArgs e) => OpenUrl(YouTubeStudioLive);

    private void StreamLogs_Click(object sender, RoutedEventArgs e)
    {
        var dir = System.IO.Path.Combine(Core.AppSettings.Folder, "stream-health");
        System.IO.Directory.CreateDirectory(dir);
        OpenUrl(dir);
    }

    // ---------- go live / end ----------

    private async void GoLive_Click(object sender, RoutedEventArgs e)
    {
        if (_streamBusy)
            return;
        if (_streamer != null)
        {
            if (MessageBox.Show(this, "Stop sending the stream?", "CamStudio Pro", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                await StopStreamAsync();
            return;
        }
        if (!StreamConfigured)
        {
            StreamEdit_Click(sender, e);
            StreamStatusText.Text = "Paste your stream key from YouTube Studio.";
            return;
        }

        _streamBusy = true;
        GoLiveButton.IsEnabled = false;
        StreamEditButton.IsEnabled = false;
        StreamStatusText.Text = "Starting…";
        try
        {
            var server = YouTubeBroadcast.PreferSecureServer(_settings.StreamUrl);
            var watch = ViewerLink.TryNormalize(_settings.StreamViewerUrl, _settings.StreamKey, out var viewerLink, out _)
                ? viewerLink : "";
            if (LiveStreamer.IsYouTubeEndpoint(server) && watch.Length > 0)
            {
                StreamStatusText.Text = "Checking the YouTube event...";
                if (await YouTubeBroadcast.HasEndedAsync(watch))
                {
                    StreamEdit_Click(sender, e);
                    StreamStatusText.Text = "This YouTube event has ended.";
                    StreamSetupErrorText.Text = "Create a new event in YouTube Studio, then paste its viewer link and stream key.";
                    StreamViewerUrlBox.SelectAll();
                    StreamViewerUrlBox.Focus();
                    return;
                }
            }

            if (!FFmpegTools.IsAvailable && !await Task.Run(FFmpegTools.Locate))
            {
                StreamStatusText.Text = "Live streaming needs FFmpeg. Set it up in the Editor.";
                return;
            }
            var encoder = await PickLiveEncoderAsync();
            var frame = _hub.Current;
            var fps = Math.Clamp(_session.Stats?.ConfiguredFps ?? 30, 24, 60);
            var url = server.TrimEnd('/') + "/" + _settings.StreamKey;
            var log = System.IO.Path.Combine(Core.AppSettings.Folder, "stream.log");
            var csvDir = System.IO.Path.Combine(Core.AppSettings.Folder, "stream-health");
            System.IO.Directory.CreateDirectory(csvDir);
            var csv = System.IO.Path.Combine(csvDir, $"stream-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.csv");
            var streamer = new LiveStreamer(FFmpegTools.FfmpegPath!, encoder, url, frame?.Width ?? 1920, frame?.Height ?? 1080,
                fps, _settings.StreamQuality, log, csv, lastStableStep: _settings.LastStreamStep);
            streamer.Changed += () => Dispatcher.BeginInvoke(UpdateStreamControls);
            _activeViewerUrl = watch;
            _streamer = streamer;
            _session.Streamer = streamer;
            streamer.Start();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            StreamStatusText.Text = "Couldn't start the stream: " + ex.Message;
        }
        finally
        {
            _streamBusy = false;
            StreamEditButton.IsEnabled = true;
            UpdateStreamControls();
        }
    }

    private static string? _liveEncoder;

    /// <summary>
    /// For live video, prefer the graphics card's encoder (NVIDIA, Intel, AMD) so the CPU stays free for
    /// decoding the phone's video; fall back to x264, then Windows' Media Foundation encoder.
    /// </summary>
    private static async Task<string> PickLiveEncoderAsync()
    {
        if (_liveEncoder != null)
            return _liveEncoder;
        var available = await FFmpegTools.EncodersAsync();
        foreach (var e in new[] { "h264_nvenc", "h264_qsv", "h264_amf", "libx264", "h264_mf" })
        {
            if (available.Contains(e) && await FFmpegTools.EncoderWorksAsync(e))
                return _liveEncoder = e;
        }
        return _liveEncoder = "libx264";
    }

    private async Task StopStreamAsync()
    {
        var streamer = _streamer;
        if (streamer == null)
            return;
        _streamBusy = true;
        UpdateStreamControls();
        _session.Streamer = null;
        await streamer.StopAsync();
        if (streamer.Elapsed > TimeSpan.FromMinutes(1))
        {
            _settings.LastStreamStep = streamer.StableStepName; // Auto starts here next time
            _settings.Save();
        }
        streamer.Dispose();
        _streamer = null;
        _streamBusy = false;
        UpdateStreamControls();
        StreamStatusText.Text = $"Stream ended after {Format(streamer.Elapsed)}.";
    }

    private void UpdateStreamControls()
    {
        var s = _streamer;
        StreamUrlBox.IsEnabled = s == null;
        StreamKeyBox.IsEnabled = s == null;
        StreamQualityCombo.IsEnabled = s == null;
        var sharing = s != null && s.State is LiveStreamer.StreamState.Live or LiveStreamer.StreamState.Reconnecting;
        StreamViewerPanel.Visibility = sharing ? Visibility.Visible : Visibility.Collapsed;
        YouTubeBroadcastPanel.Visibility = s?.IsYouTube == true && s.State != LiveStreamer.StreamState.Failed
            ? Visibility.Visible : Visibility.Collapsed;
        if (StreamViewerLinkBox.Text != _activeViewerUrl)
            StreamViewerHint.Text = "";
        StreamViewerLinkBox.Text = _activeViewerUrl;
        StreamViewerLinkBox.Visibility = _activeViewerUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StreamViewerCopyButton.IsEnabled = _activeViewerUrl.Length > 0;
        StreamViewerOpenButton.IsEnabled = _activeViewerUrl.Length > 0;
        if (_activeViewerUrl.Length == 0) StreamViewerHint.Text = "Add a watch link in Edit.";
        GoLiveButton.IsEnabled = !_streamBusy && !_quitting;
        GoLiveLabel.Text = s != null ? "Stop stream" : "Start stream";
        GoLiveButton.Style = (Style)FindResource(s != null ? "LiveStopAction" : "LiveAction");

        if (s == null)
        {
            LiveBadge.Visibility = Visibility.Collapsed;
            if (!_streamBusy && StreamSetupPanel.Visibility != Visibility.Visible && !StreamStatusText.Text.StartsWith("Stream ended")
                && !StreamStatusText.Text.StartsWith("Couldn't") && !StreamStatusText.Text.StartsWith("Live streaming needs"))
                StreamStatusText.Text = StreamConfigured
                    ? $"Ready · {Host(_settings.StreamUrl)} · {QualityLabel(_settings.StreamQuality)}"
                    : "Not set up";
            StreamEditButton.Content = StreamConfigured ? "Edit" : "Set up";
            return;
        }

        if (s.State == LiveStreamer.StreamState.Failed)
        {
            if (_streamBusy) return;
            // Give up: tear down and explain.
            var reason = s.LastError ?? "The stream stopped.";
            _ = StopStreamAsync().ContinueWith(_ => Dispatcher.BeginInvoke(() => StreamStatusText.Text = reason));
            return;
        }

        LiveBadge.Visibility = Visibility.Visible;
        LiveText.Text = s.State switch
        {
            LiveStreamer.StreamState.Live => "SENDING " + Format(s.Elapsed),
            LiveStreamer.StreamState.Reconnecting => "RECONNECTING",
            _ => "CONNECTING",
        };
        var rate = double.IsFinite(s.UploadKbps) && s.UploadKbps > 0 ? $" · {s.UploadKbps / 1000:0.0} Mbps" : "";
        var health = s.Lag > 1 ? $" · {s.Lag:0.0} s behind" : "";
        StreamStatusText.Text = s.State switch
        {
            LiveStreamer.StreamState.Live when !s.PhoneSignal => $"Sending to {Host(_settings.StreamUrl)} · {s.Current.Name} · waiting for video",
            LiveStreamer.StreamState.Live => $"Sending to {Host(_settings.StreamUrl)} · {s.Current.Name}{rate}{health}",
            LiveStreamer.StreamState.Reconnecting => "Reconnecting… " + (s.LastError ?? ""),
            _ => $"Connecting to {Host(_settings.StreamUrl)} · {s.Current.Name}…",
        };
        if (s.Notice != null)
            StreamStatusText.Text += "\n" + s.Notice;
    }

    /// <summary>Asks before quitting while live or with unsaved edits. False = the user cancelled.</summary>
    public bool ConfirmQuit()
    {
        if (_streamer != null)
        {
            ((App)Application.Current).ShowMain();
            if (MessageBox.Show(this, "Stop sending the stream and quit?", "CamStudio Pro",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return false;
        }
        return ConfirmEditorClose();
    }

    private static string Format(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u)
        ? (LiveStreamer.IsYouTubeEndpoint(url) ? "YouTube" : u.Host) : url;

    private static string QualityLabel(string q) => q is "4K" or "1440p" or "1080p" or "720p" or "480p" ? "up to " + q : "Auto quality";
}
