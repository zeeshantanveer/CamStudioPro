using System.Windows;
using System.Windows.Controls;
using CamStudio.Desktop.Media;

namespace CamStudio.Desktop;

public partial class MainWindow
{
    private bool _studioReady;
    private bool _carouselSelection;
    private string _carouselRequest = "";
    private long _nextBackgroundAt;
    private StudioMicrophone? _previewMic;

    private void InitStudio()
    {
        _settings.BackgroundPlaylist = (_settings.BackgroundPlaylist ?? [])
            .Where(id => FindBackground(id) != null).Distinct().ToList();
        RefreshPlaylist(0);
        BackgroundIntervalCombo.SelectedItem = BackgroundIntervalCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => (string)item.Tag == _settings.BackgroundIntervalSeconds.ToString());
        if (BackgroundIntervalCombo.SelectedIndex < 0) BackgroundIntervalCombo.SelectedIndex = 2;
        StudioMicToggle.IsChecked = _settings.ShowVirtualMic;
        StudioMicFollow.IsChecked = _settings.VirtualMicFollowsAudio;
        StudioMicRecord.IsChecked = _settings.VirtualMicInRecording;
        StudioMicStream.IsChecked = _settings.VirtualMicInStream;
        StudioLabelFont.SelectedIndex = Math.Clamp(_settings.VirtualMicLabelFont, 0, 2);
        StudioLabelSize.SelectedIndex = Math.Clamp(_settings.VirtualMicLabelSize, 0, 2);
        StudioLabelBold.IsChecked = _settings.VirtualMicLabelBold;
        UpdateStudioLabelColors();
        StudioMicLabel.Text = _settings.VirtualMicText ?? "";
        StudioMicStyleCombo.SelectedIndex = Math.Clamp(_settings.VirtualMicStyle, 0, 1);
        StudioMicSize.SelectedIndex = Math.Clamp(_settings.VirtualMicSize, 0, 2);
        _studioReady = true;
        ApplyStudioMic();
    }

    private ComboBoxItem? FindBackground(int id) => VideoBackgroundCombo.Items.OfType<ComboBoxItem>()
        .FirstOrDefault(item => (string)item.Tag == id.ToString());

    private void RefreshPlaylist(int index)
    {
        BackgroundPlaylistBox.ItemsSource = _settings.BackgroundPlaylist.Select(id => FindBackground(id)?.Content).ToArray();
        BackgroundPlaylistBox.SelectedIndex = Math.Min(index, _settings.BackgroundPlaylist.Count - 1);
    }

    private void CarouselAdd_Click(object sender, RoutedEventArgs e)
    {
        if (VideoBackgroundCombo.SelectedItem is not ComboBoxItem item || !int.TryParse((string)item.Tag, out var id)) return;
        var list = _settings.BackgroundPlaylist;
        if (!list.Contains(id)) { list.Add(id); _settings.Save(); }
        RefreshPlaylist(list.IndexOf(id));
    }

    private void CarouselRemove_Click(object sender, RoutedEventArgs e)
    {
        var index = BackgroundPlaylistBox.SelectedIndex;
        if (index < 0) return;
        _settings.BackgroundPlaylist.RemoveAt(index);
        RefreshPlaylist(index);
        _settings.Save();
    }

    private void CarouselUp_Click(object sender, RoutedEventArgs e) => MoveBackground(-1);
    private void CarouselDown_Click(object sender, RoutedEventArgs e) => MoveBackground(1);

    private void MoveBackground(int step)
    {
        var from = BackgroundPlaylistBox.SelectedIndex;
        var to = from + step;
        var list = _settings.BackgroundPlaylist;
        if (from < 0 || to < 0 || to >= list.Count) return;
        (list[from], list[to]) = (list[to], list[from]);
        RefreshPlaylist(to);
        _settings.Save();
    }

    private void CarouselPrevious_Click(object sender, RoutedEventArgs e) => StepBackground(-1);
    private void CarouselNext_Click(object sender, RoutedEventArgs e) => StepBackground(1);
    private void CarouselShow_Click(object sender, RoutedEventArgs e) => ShowBackground(BackgroundPlaylistBox.SelectedIndex);

    private void StepBackground(int step)
    {
        if (_quitting || _session.VideoOptions is not { } report || _videoOptionsPending.Length > 0) return;
        var list = _settings.BackgroundPlaylist;
        if (list.Count == 0) { VideoOptionsStatus.Text = "Add a background first."; return; }
        var current = list.IndexOf(report.Background);
        var next = current < 0 ? (step > 0 ? 0 : list.Count - 1) : (current + step + list.Count) % list.Count;
        ShowBackground(next);
    }

    private void ShowBackground(int index)
    {
        var list = _settings.BackgroundPlaylist;
        if (_quitting || index < 0 || index >= list.Count || _videoOptionsPending.Length > 0 ||
            _session.VideoOptions is not { } report || list[index] == report.Background) return;
        _carouselSelection = true;
        try { VideoBackgroundCombo.SelectedItem = FindBackground(list[index]); }
        finally { _carouselSelection = false; }
        BackgroundPlaylistBox.SelectedIndex = index;
        SendVideoOptions(false);
        _carouselRequest = _videoOptionsPending;
        ScheduleBackground();
    }

    private void CarouselAuto_Changed(object sender, RoutedEventArgs e)
    {
        if (_studioReady) ScheduleBackground();
    }

    private void CarouselInterval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_studioReady || BackgroundIntervalCombo.SelectedItem is not ComboBoxItem item) return;
        _settings.BackgroundIntervalSeconds = int.Parse((string)item.Tag);
        _settings.Save();
        ScheduleBackground();
    }

    private void ScheduleBackground() => _nextBackgroundAt = Environment.TickCount64 +
        Math.Clamp(_settings.BackgroundIntervalSeconds, 10, 300) * 1000L;

    private void TickCarousel()
    {
        if (AutoBackgroundToggle.IsChecked != true) return;
        if (!_session.CameraEnabled || _settings.BackgroundPlaylist.Count < 2) { ScheduleBackground(); return; }
        if (Environment.TickCount64 >= _nextBackgroundAt && _videoOptionsPending.Length == 0) StepBackground(1);
    }

    private void StudioMic_Changed(object sender, RoutedEventArgs e)
    {
        if (!_studioReady) return;
        _settings.ShowVirtualMic = StudioMicToggle.IsChecked == true;
        _settings.VirtualMicFollowsAudio = StudioMicFollow.IsChecked == true;
        _settings.VirtualMicInRecording = StudioMicRecord.IsChecked == true;
        _settings.VirtualMicInStream = StudioMicStream.IsChecked == true;
        _settings.VirtualMicLabelFont = StudioLabelFont.SelectedIndex;
        _settings.VirtualMicLabelSize = StudioLabelSize.SelectedIndex;
        _settings.VirtualMicLabelBold = StudioLabelBold.IsChecked == true;
        _settings.VirtualMicText = StudioMicLabel.Text.Trim();
        _settings.VirtualMicStyle = StudioMicStyleCombo.SelectedIndex;
        _settings.VirtualMicSize = StudioMicSize.SelectedIndex;
        _settings.Save();
        ApplyStudioMic();
    }

    private void UpdateStudioLabelColors()
    {
        StudioLabelInk.Content = $"Text color  {LabelColor(_settings.VirtualMicLabelInk, "#FFFFFF"):X6}";
        StudioLabelLeft.Content = $"Gradient left  {LabelColor(_settings.VirtualMicLabelLeft, "#18243C"):X6}";
        StudioLabelRight.Content = $"Gradient right  {LabelColor(_settings.VirtualMicLabelRight, "#40588B"):X6}";
    }

    private static int LabelColor(string? value, string fallback)
    {
        if (value is not { Length: 7 } || value[0] != '#' ||
            !int.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out int rgb))
            return Convert.ToInt32(fallback[1..], 16);
        return rgb;
    }

    private void StudioLabelColor_Click(object sender, RoutedEventArgs e)
    {
        var part = (string)((Button)sender).Tag;
        int rgb = part switch
        {
            "left" => LabelColor(_settings.VirtualMicLabelLeft, "#18243C"),
            "right" => LabelColor(_settings.VirtualMicLabelRight, "#40588B"),
            _ => LabelColor(_settings.VirtualMicLabelInk, "#FFFFFF")
        };
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(rgb | unchecked((int)0xFF000000)),
            FullOpen = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        string color = $"#{dialog.Color.ToArgb() & 0xFFFFFF:X6}";
        switch (part)
        {
            case "left": _settings.VirtualMicLabelLeft = color; break;
            case "right": _settings.VirtualMicLabelRight = color; break;
            default: _settings.VirtualMicLabelInk = color; break;
        }
        UpdateStudioLabelColors();
        _settings.Save();
        ApplyStudioMic();
    }

    private void ApplyStudioMic()
    {
        var label = _settings.VirtualMicText ?? "";
        _session.StudioMic = _settings.ShowVirtualMic ? new StudioMicrophone(
            label[..Math.Min(label.Length, 60)], new StudioLabelStyle(
                StudioLabelFont.SelectedItem is ComboBoxItem font ? (string)font.Content : "Segoe UI",
                _settings.VirtualMicLabelSize, _settings.VirtualMicLabelBold,
                LabelColor(_settings.VirtualMicLabelInk, "#FFFFFF"), LabelColor(_settings.VirtualMicLabelLeft, "#18243C"),
                LabelColor(_settings.VirtualMicLabelRight, "#40588B")),
            _settings.VirtualMicStyle == 1 ? StudioMicStyle.Foam : StudioMicStyle.Mesh,
            _settings.VirtualMicSize, _settings.VirtualMicFollowsAudio,
            _settings.VirtualMicInRecording, _settings.VirtualMicInStream) : null;
        _previewVersion = -1;
    }
}
