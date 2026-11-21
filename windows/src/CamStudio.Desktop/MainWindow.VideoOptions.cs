using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace CamStudio.Desktop;
public partial class MainWindow
{
    private string _videoOptionsPending = "";
    private long _videoOptionsSentAt;
    private bool _videoOptionsInitialized;

    private void ApplyVideoOptions_Click(object sender, RoutedEventArgs e) => SendVideoOptions(true);

    private void VideoBackground_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_videoOptionsInitialized && !_carouselSelection)
        {
            ScheduleBackground();
            SendVideoOptions(false);
        }
    }

    private void NaturalBackground_Changed(object sender, RoutedEventArgs e)
    {
        if (_videoOptionsInitialized) SendVideoOptions(false);
    }

    private void SendVideoOptions(bool includeQuality)
    {
        if (_session.VideoOptions == null || _quitting) return;
        var background = int.Parse((string)((ComboBoxItem)VideoBackgroundCombo.SelectedItem).Tag);
        var changes = new JsonObject { ["background"] = background, ["natural"] = NaturalBackgroundToggle.IsChecked == true };
        if (includeQuality) changes["resolution"] = int.Parse((string)((ComboBoxItem)VideoQualityCombo.SelectedItem).Tag);
        _videoOptionsPending = Guid.NewGuid().ToString("N");
        _videoOptionsSentAt = Environment.TickCount64;
        ApplyVideoOptionsButton.IsEnabled = false;
        VideoOptionsStatus.Text = includeQuality ? "Applying quality…" : "Changing background…";
        _session.SetVideoOptions(changes, _videoOptionsPending);
    }

    private void UpdateVideoOptions()
    {
        var report = _session.VideoOptions;
        VideoOptionsPanel.IsEnabled = report != null && !_quitting;
        if (report == null)
        {
            VideoOptionsStatus.Text = _session.Phone == null ? "Connect a phone" : "Waiting for phone settings…";
            return;
        }
        if (!_videoOptionsInitialized)
        {
            foreach (ComboBoxItem item in VideoQualityCombo.Items)
                if ((string)item.Tag == report.Resolution.ToString()) VideoQualityCombo.SelectedItem = item;
            foreach (ComboBoxItem item in VideoBackgroundCombo.Items)
                if ((string)item.Tag == report.Background.ToString()) VideoBackgroundCombo.SelectedItem = item;
            NaturalBackgroundToggle.IsChecked = report.Natural;
            ApplyVideoOptionsButton.IsEnabled = true;
            _videoOptionsInitialized = true;
            VideoOptionsStatus.Text = "";
        }
        if (_videoOptionsPending.Length > 0)
        {
            if (report.Acknowledgement == _videoOptionsPending)
            {
                if (_carouselRequest == _videoOptionsPending)
                {
                    _carouselRequest = "";
                    if (report.Error.Length > 0)
                    {
                        AutoBackgroundToggle.IsChecked = false;
                        _carouselSelection = true;
                        try { VideoBackgroundCombo.SelectedItem = FindBackground(report.Background); }
                        finally { _carouselSelection = false; }
                    }
                    else ScheduleBackground();
                }
                _videoOptionsPending = "";
                ApplyVideoOptionsButton.IsEnabled = true;
                VideoOptionsStatus.Text = report.Error.Length > 0 ? report.Error : "";
            }
            else if (Environment.TickCount64 - _videoOptionsSentAt >= 5000)
            {
                if (_carouselRequest == _videoOptionsPending)
                {
                    _carouselRequest = "";
                    AutoBackgroundToggle.IsChecked = false;
                }
                _videoOptionsPending = "";
                ApplyVideoOptionsButton.IsEnabled = true;
                VideoOptionsStatus.Text = "No response from phone. Try again.";
            }
        }
    }
}
