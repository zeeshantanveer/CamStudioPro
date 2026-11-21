using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CamStudio.Desktop.Core;
using CamStudio.Desktop.Media;
using CamStudio.Desktop.Platform;
using QRCoder;

namespace CamStudio.Desktop;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly FrameHub _hub = new();
    private VirtualCameraOutput? _virtualCamera;
    private readonly AudioSink _audio = new();
    private readonly AdbBridge _adb = new();
    private readonly SessionServer _session;
    private readonly DiscoveryResponder _discovery;
    private readonly DispatcherTimer _previewTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _slowTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private WriteableBitmap? _preview;
    private byte[] _previewPixels = Array.Empty<byte>();
    private long _previewVersion = -1;
    private string _lastDisconnect = "";
    private bool _quitting;
    private VideoRecorder? _recorder;
    private bool _recordingBusy;
    private bool _voiceControlsReady;

    public bool StartMinimized => _settings.StartMinimized;

    public MainWindow()
    {
        InitializeComponent();

        _session = new SessionServer(_settings, _hub, _audio, ApproveAsync);
        _discovery = new DiscoveryResponder(_settings);
        InitVoiceEnhancement();
        InitBeauty();

        PcNameText.Text = Environment.MachineName;
        MirrorToggle.IsChecked = _settings.MirrorPreview;
        StartMinimizedToggle.IsChecked = _settings.StartMinimized;
        StartupRegistration.RefreshPath();
        _ = LoadStartupAsync();
        if (PackageInfo.IsPackaged) VirtualCameraButton.Content = "Retry camera output";
        InitTheme();
        InitNavigation();
        InitStream();
        InitShare();
        InitStudio();
        VersionText.Text = $"CamStudio Pro {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}" +
            (PackageInfo.IsPackaged ? "" : " · desktop build");

        UpdatePreviewState();
        if (!App.UiPreview)
        {
            _session.Start();
            _discovery.Start();
            _adb.Start();
            _ = StartVirtualCameraAsync(false);
        }

        _session.Connected += _ => Dispatcher.BeginInvoke(OnSessionChanged);
        _session.Disconnected += (_, reason) => Dispatcher.BeginInvoke(() =>
        {
            _lastDisconnect = reason;
            OnSessionChanged();
        });
        _session.Updated += () => Dispatcher.BeginInvoke(UpdateStats);
        _adb.Changed += () => Dispatcher.BeginInvoke(UpdateUsb);

        InitAudioOptions();
        RefreshPairing();
        RefreshTrusted();
        UpdateUsb();
        var errors = new[] { _session.Error, _discovery.Error }.Where(e => e != null).ToList();
        if (errors.Count > 0)
        {
            NetworkErrorText.Text = "Network problem. Another copy of CamStudio may be running.";
            NetworkErrorText.ToolTip = string.Join("\n", errors);
            NetworkErrorText.Visibility = Visibility.Visible;
        }

        _previewTimer.Tick += (_, _) => RenderPreview();
        _previewTimer.Start();
        _slowTimer.Tick += (_, _) => { SlowTick(); UpdateVideoOptions(); };
        _slowTimer.Start();

        OnSessionChanged();
    }

    private async void EnableVirtualCamera_Click(object sender, RoutedEventArgs e) => await StartVirtualCameraAsync(true);

    private async Task StartVirtualCameraAsync(bool install)
    {
        VirtualCameraButton.IsEnabled = false;
        try
        {
            if (_virtualCamera != null) return;
            if (install && !PackageInfo.IsPackaged) await VirtualCameraOutput.RegisterAsync();
            var camera = await Task.Run(() => VirtualCameraOutput.Start(_hub));
            if (_quitting) { camera.Dispose(); return; }
            _virtualCamera = camera;
            VirtualCameraStatus.Text = "Camera ready. Restart Zoom and select CamStudio Pro Windows Virtual Camera. Keep CamStudio Pro open.";
        }
        catch (Exception ex)
        {
            VirtualCameraStatus.Text = "Camera unavailable: " + ex.Message +
                (PackageInfo.IsPackaged ? "" : " Click Enable camera for Zoom to install or repair it.");
        }
        finally { VirtualCameraButton.IsEnabled = _virtualCamera == null; }
    }

    // ---------- session ----------

    private Task<bool> ApproveAsync(PhoneInfo phone, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.BeginInvoke(() =>
        {
            var dialog = new ApprovalWindow(phone);
            if (IsVisible)
                dialog.Owner = this;
            var reg = ct.Register(() => Dispatcher.BeginInvoke(dialog.Close));
            dialog.Closed += (_, _) =>
            {
                reg.Dispose();
                tcs.TrySetResult(dialog.Allowed);
                RefreshTrusted();
            };
            dialog.Show();
            dialog.Activate();
        });
        return tcs.Task;
    }

    private void OnSessionChanged()
    {
        AutoBackgroundToggle.IsChecked = false;
        _carouselRequest = "";
        _videoOptionsPending = "";
        _videoOptionsInitialized = false;
        UpdateVideoOptions();
        ResetBeautySession();
        var phone = _session.Phone;
        var live = phone != null;
        DisconnectButton.IsEnabled = live;
        PhoneBadge.Visibility = live ? Visibility.Visible : Visibility.Collapsed;

        if (live)
        {
            var via = phone!.Transport == "usb" ? "USB" : "Wi‑Fi";
            PhoneBadgeText.Text = $"{phone.Name} · {via}";
            SetStatusBrush("Good");
            StatusText.Text = $"Connected to {phone.Name}";
            PhoneDetailText.Text = string.IsNullOrEmpty(phone.Model) ? $"Connected over {via}" : $"{phone.Model} · connected over {via}";
            ((App)Application.Current).UpdateTray("connected to " + phone.Name);
        }
        else
        {
            SetStatusBrush("Accent");
            StatusText.Text = "Waiting for a phone";
            PhoneDetailText.Text = string.IsNullOrEmpty(_lastDisconnect)
                ? ""
                : _lastDisconnect + ". Open CamStudio on your phone to reconnect.";
            ((App)Application.Current).UpdateTray("waiting for a phone");
            DelayText.Text = FpsText.Text = BitrateText.Text = DroppedText.Text = ResolutionText.Text = "—";
        }

        UpdatePreviewState();
        UpdateShareControls();
        RefreshTrusted();
    }

    private void UpdateStats()
    {
        UpdateBeauty();
        UpdateVideoOptions();
        var stats = _session.Stats;
        var state = _session.State;
        if (_session.Phone == null || stats == null)
            return;

        if (state?.EncodeMs is int enc && state.RttMs is int rtt)
            DelayText.Text = $"{enc + rtt / 2 + stats.PipelineMs} ms";
        else
            DelayText.Text = stats.PipelineMs > 0 ? $"{stats.PipelineMs} ms+" : "—";

        FpsText.Text = $"{Math.Round(stats.RecvFps):0} / {stats.ConfiguredFps}";
        BitrateText.Text = stats.BitrateBps > 0 ? $"{stats.BitrateBps / 1_000_000.0:0.0} Mbps" : "—";
        DroppedText.Text = stats.TotalFrames > 0 ? $"{100.0 * stats.DroppedFrames / stats.TotalFrames:0.0} %" : "—";
        ResolutionText.Text = stats.Width > 0 ? $"{stats.Width}×{stats.Height}" : "—";

        var details = new List<string>();
        if (state?.Battery is int b)
            details.Add($"battery {b}%{(state.Charging == true ? " charging" : "")}");
        if (state?.TempC is double temp)
            details.Add($"{temp:0}°C");
        if (state?.WifiRssi is int rssi && _session.Phone.Transport != "usb")
            details.Add($"Wi‑Fi {rssi} dBm{(state.WifiBand != null ? " " + state.WifiBand : "")}");
        if (state?.Muted == true)
            details.Add("mic muted");
        if (state?.Paused == true)
            details.Add("video paused");
        var head = string.IsNullOrEmpty(_session.Phone.Model) ? _session.Phone.Name : _session.Phone.Model;
        PhoneDetailText.Text = details.Count == 0 ? head : head + " · " + string.Join(" · ", details);

        UpdatePreviewState();
    }

    private void SetStatusBrush(string key)
    {
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key);
        RailStatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key);
        RailStatusDot.ToolTip = StatusText.Text;
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) => DisconnectPhone();

    public void DisconnectPhone() => _session.Disconnect();

    // ---------- preview ----------

    private void RenderPreview()
    {
        var mic = _session.ActiveStudioMic;
        if (!ReferenceEquals(mic, _previewMic))
        {
            _previewMic = mic;
            _previewVersion = -1;
        }
        var version = _hub.Version;
        if (version == _previewVersion || !IsVisible || WindowState == WindowState.Minimized)
            return;
        _previewVersion = version;

        var frame = _hub.Current;
        if (frame == null)
            return;

        var scale = Math.Min(1.0, 960.0 / Math.Max(frame.Width, frame.Height));
        var w = Math.Max(2, (int)(frame.Width * scale));
        var h = Math.Max(2, (int)(frame.Height * scale));
        if (_preview == null || _preview.PixelWidth != w || _preview.PixelHeight != h)
        {
            _preview = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            _previewPixels = new byte[w * h * 4];
            PreviewImage.Source = _preview;
        }

        Nv12Scaler.ToBgra(frame, _previewPixels, w, h, _settings.MirrorPreview && !_session.Scene.Sharing);
        mic?.ApplyPreview(_previewPixels, w, h);
        _preview.WritePixels(new Int32Rect(0, 0, w, h), _previewPixels, w * 4, 0);
    }

    /// <summary>Shows the live picture, or a short status message when no video is flowing.</summary>
    private void UpdatePreviewState()
    {
        UpdateCameraControls();
        var phone = _session.Phone;
        PairCard.Visibility = phone == null ? Visibility.Visible : Visibility.Collapsed;
        string? title = null, detail = null;
        if (_session.Scene.Sharing && _hub.IsShowingLive)
        {
            // Sharing the screen: always show what goes out, with or without the camera.
        }
        else if (phone != null && !_session.CameraRequested)
        {
            title = "Camera is off";
            detail = _session.CameraError ?? "Connected in standby. Select Open camera when you need it.";
        }
        else if (phone != null && _session.State?.Paused == true)
        {
            title = "Video paused";
            detail = $"{phone.Name} paused the camera.";
        }
        else if (phone != null && !_hub.IsShowingLive)
        {
            title = "Starting camera…";
            detail = $"Connected to {phone.Name}.";
        }
        else if (phone == null)
        {
            title = "No phone connected";
            detail = "Open CamStudio Pro on your phone, then tap Connect.";
        }

        var waiting = title != null;
        PreviewOverlay.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        PreviewImage.Visibility = waiting ? Visibility.Collapsed : Visibility.Visible;
        if (waiting)
        {
            PreviewTitle.Text = title;
            PreviewDetail.Text = detail;
            _previewVersion = -1; // redraw the first live frame as soon as video resumes
        }
    }

    private void Mirror_Changed(object sender, RoutedEventArgs e)
    {
        _settings.MirrorPreview = MirrorToggle.IsChecked == true;
        _settings.Save();
        _previewVersion = -1;
    }

    // ---------- pairing ----------

    private void RefreshPairing()
    {
        var ips = NetInfo.LocalIPv4();
        var hosts = string.Join(",", ips.Select(i => i.ToString()));
        var uri = $"camstudio://pair?v={Protocol.Version}&h={Uri.EscapeDataString(hosts)}&p={Protocol.SessionPort}" +
                  $"&n={Uri.EscapeDataString(Environment.MachineName)}&id={_settings.PcId}&k={_settings.PairKey}";

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(6, new byte[] { 0, 0, 0 }, new byte[] { 255, 255, 255 }, false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        QrImage.Source = image;

        AddressText.Text = ips.Count == 0
            ? "No network connection"
            : string.Join("\n", ips.Take(3).Select(i => $"{i}:{Protocol.SessionPort}"));
        _pairingHosts = hosts;
    }

    private string _pairingHosts = "";

    private void NewPairCode_Click(object sender, RoutedEventArgs e)
    {
        _settings.RotatePairKey();
        RefreshPairing();
    }

    private void RefreshTrusted()
    {
        TrustedList.Children.Clear();
        var phones = _settings.Trusted.OrderByDescending(t => t.LastUsed).ToList();
        NoTrustedText.Visibility = phones.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var t in phones)
        {
            var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var forget = new Button
            {
                Content = "Forget",
                Style = (Style)FindResource("GhostButton"),
                MinHeight = 32,
                Padding = new Thickness(12, 0, 12, 0),
                FontSize = 12,
            };
            System.Windows.Automation.AutomationProperties.SetName(forget, "Forget " + t.Name);
            var id = t.DeviceId;
            forget.Click += (_, _) =>
            {
                _settings.Forget(id);
                if (_session.Phone?.DeviceId == id)
                    _session.Disconnect();
                RefreshTrusted();
            };
            DockPanel.SetDock(forget, Dock.Right);
            row.Children.Add(forget);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = t.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var connected = _session.Phone?.DeviceId == t.DeviceId;
            var status = new TextBlock { Text = connected ? "Connected now" : "Last used " + Ago(t.LastUsed), FontSize = 12 };
            status.SetResourceReference(TextBlock.ForegroundProperty, connected ? "Good" : "TextDim");
            text.Children.Add(status);
            row.Children.Add(text);
            TrustedList.Children.Add(row);
        }
    }

    private static string Ago(DateTime when)
    {
        var days = (DateTime.Today - when.Date).Days;
        return days switch
        {
            <= 0 => "today",
            1 => "yesterday",
            _ => $"{days} days ago",
        };
    }

    // ---------- USB ----------

    private void UpdateUsb()
    {
        if (_adb.AdbPath == null)
        {
            UsbStatusText.Text = "adb not found";
            UsbDetailText.Text = "USB needs Android platform-tools (adb.exe). Wi‑Fi works without it.";
            return;
        }

        var devices = _adb.Devices;
        var ready = devices.Where(d => d.State == "device").ToList();
        if (ready.Count > 0)
        {
            UsbStatusText.Text = ready.Count == 1 ? $"{ready[0].Model} ready over USB" : $"{ready.Count} phones ready over USB";
            UsbDetailText.Text = "On the phone: Connect › USB cable.";
        }
        else if (devices.Any(d => d.State == "unauthorized"))
        {
            UsbStatusText.Text = "Allow USB debugging on the phone";
            UsbDetailText.Text = "Unlock the phone and accept the prompt.";
        }
        else
        {
            UsbStatusText.Text = "No phone on USB";
            UsbDetailText.Text = "Turn on USB debugging on the phone and plug it in.";
        }
    }

    // ---------- audio ----------

    private void InitAudioOptions()
    {
        var outputs = AudioSink.ListOutputs();
        if (!_settings.AudioOutputChosen)
        {
            var cable = AudioSink.FindVirtualCable();
            if (cable != null)
            {
                _settings.AudioOutputId = cable.Id;
                _settings.AudioOutputChosen = true;
                _settings.Save();
            }
        }

        if (outputs.All(o => o.Id != _settings.AudioOutputId))
            _settings.AudioOutputId = "";
        _audioReady = false;
        AudioOutputCombo.Items.Clear();
        AudioOutputCombo.Items.Add(new ComboBoxItem { Content = "Off", Tag = "" });
        foreach (var o in outputs)
            AudioOutputCombo.Items.Add(new ComboBoxItem { Content = o.Name, Tag = o.Id });
        AudioOutputCombo.SelectedItem = AudioOutputCombo.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == _settings.AudioOutputId);
        _audioReady = true;
        _audio.SetDevice(_settings.AudioOutputId);
    }

    private bool _audioReady;

    private void AudioOutput_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_audioReady || AudioOutputCombo.SelectedItem is not ComboBoxItem item)
            return;
        _settings.AudioOutputId = (string)item.Tag;
        _settings.AudioOutputChosen = true;
        _settings.Save();
        _audio.SetDevice(_settings.AudioOutputId);
    }

    // ---------- periodic ----------

    private void InitVoiceEnhancement()
    {
        VoiceEnhancementToggle.IsChecked = _settings.VoiceEnhancement;
        AutomaticVoiceVolumeToggle.IsChecked = _settings.AutomaticVoiceVolume;
        NoiseReductionCombo.SelectedIndex = _settings.NoiseReduction switch { "Low" => 0, "Strong" => 2, _ => 1 };
        VoicePresetCombo.SelectedIndex = _settings.VoicePreset == "Broadcast" ? 1 : 0;
        VoiceEnhancementOptionsPanel.IsEnabled = _settings.VoiceEnhancement;
        _voiceControlsReady = true;
        ApplyVoiceEnhancement();
    }

    private void VoiceEnhancement_Changed(object sender, RoutedEventArgs e)
    {
        if (!_voiceControlsReady) return;
        ApplyVoiceEnhancement();
        _settings.Save();
    }

    private void ApplyVoiceEnhancement()
    {
        _settings.VoiceEnhancement = VoiceEnhancementToggle.IsChecked == true;
        _settings.AutomaticVoiceVolume = AutomaticVoiceVolumeToggle.IsChecked == true;
        _settings.NoiseReduction = ((ComboBoxItem)NoiseReductionCombo.SelectedItem).Content.ToString()!;
        _settings.VoicePreset = ((ComboBoxItem)VoicePresetCombo.SelectedItem).Content.ToString()!;
        VoiceEnhancementOptionsPanel.IsEnabled = _settings.VoiceEnhancement;
        _session.EnhancementOptions = new(_settings.VoiceEnhancement, _settings.NoiseReduction,
            _settings.AutomaticVoiceVolume, _settings.VoicePreset);
        VoiceEnhancementStatusText.Text = "";
    }

    private void SlowTick()
    {
        TickCarousel();
        var voiceStatus = _session.VoiceStatus;
        VoiceEnhancementStatusText.Text = _settings.VoiceEnhancement &&
            (voiceStatus.StartsWith("Unavailable") || voiceStatus.StartsWith("Bypassed")) ? voiceStatus : "";
        UpdateRecordingControls();
        UpdatePreviewState();
        UpdateStreamControls();
        AudioMeter.Width = _session.Phone == null ? 0 : Math.Min(1, _audio.Level * 1.6) * ((FrameworkElement)AudioMeter.Parent).ActualWidth;

        // network changes (new Wi-Fi, VPN) change the QR code contents
        var hosts = string.Join(",", NetInfo.LocalIPv4().Select(i => i.ToString()));
        if (hosts != _pairingHosts)
            RefreshPairing();

        if (_session.CameraEnabled && !_hub.IsShowingLive && _session.State?.Paused != true)
            _session.RequestKeyframe();
    }

    private bool _startupToggleReady;

    private async Task LoadStartupAsync() => ShowStartup(await StartupRegistration.GetAsync());

    private void ShowStartup(StartupRegistration.State state)
    {
        _startupToggleReady = false;
        StartWithWindowsToggle.IsChecked = state.Enabled;
        StartWithWindowsToggle.IsEnabled = state.CanChange;
        StartupNoteText.Text = state.Note ?? "Opens in the notification area so phones can reconnect.";
        _startupToggleReady = true;
    }

    private async void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (!_startupToggleReady)
            return;
        try
        {
            ShowStartup(await StartupRegistration.SetAsync(StartWithWindowsToggle.IsChecked == true));
        }
        catch (Exception ex)
        {
            App.Log(ex);
            MessageBox.Show("Couldn't change the Windows startup setting: " + ex.Message, "CamStudio Pro",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowStartup(await StartupRegistration.GetAsync());
        }
    }

    private void StartMinimized_Changed(object sender, RoutedEventArgs e)
    {
        _settings.StartMinimized = StartMinimizedToggle.IsChecked == true;
        _settings.Save();
    }

    // ---------- lifetime ----------

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_quitting)
        {
            e.Cancel = true;
            Hide();
            ((App)Application.Current).HiddenToTray();
            return;
        }
        base.OnClosing(e);
    }

    public async Task ShutdownAsync()
    {
        if (_quitting) return;
        _quitting = true;
        SetBeautyComparison(false);
        _beautyTimer.Stop();
        RecordButton.IsEnabled = false;
        if (_recorder != null)
        {
            _recorder.Finish();
            try { await _recorder.Completion; } catch { /* Reported by ObserveRecordingAsync. */ }
        }
        _previewTimer.Stop();
        _slowTimer.Stop();
        if (_streamer != null)
            await StopStreamAsync();
        _virtualCamera?.Dispose();
        _session.Scene.Dispose();
        _session.Dispose();
        _discovery.Dispose();
        _adb.Dispose();
        _audio.Dispose();
        Close();
    }

    private void UpdateRecordingControls()
    {
        RecordButton.IsEnabled = !_quitting && !_recordingBusy &&
            (_recorder != null || ((_session.Scene.Sharing || _session.CameraEnabled) && _hub.IsShowingLive));
        RecBadge.Visibility = _recorder != null ? Visibility.Visible : Visibility.Collapsed;
        PauseRecordingButton.Visibility = _recorder != null && !_recordingBusy && !_quitting
            ? Visibility.Visible : Visibility.Collapsed;
        if (_recorder != null)
        {
            var paused = _recorder.IsPaused;
            PauseRecordingButton.Content = paused ? "Resume" : "Pause";
            var action = paused ? "Resume recording" : "Pause recording";
            System.Windows.Automation.AutomationProperties.SetName(PauseRecordingButton, action);
            PauseRecordingButton.ToolTip = action;
            RecText.Text = $"{(paused ? "PAUSED" : "REC")} {_recorder.Elapsed:hh\\:mm\\:ss}";
        }
    }

    private void PauseRecording_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder is not { } recorder || _recordingBusy || _quitting) return;
        recorder.SetPaused(!recorder.IsPaused);
        UpdateRecordingControls();
    }

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder != null)
        {
            _recordingBusy = true;
            UpdateRecordingControls();
            ShowRecordingResult("Saving recording…", null);
            _recorder.Finish();
            return;
        }
        var frame = _hub.Current;
        if ((!_session.Scene.Sharing && !_session.CameraEnabled) || !_hub.IsShowingLive || frame == null) return;
        _recordingBusy = true;
        RecordButton.IsEnabled = false;
        VideoRecorder? recorder = null;
        try
        {
            recorder = new VideoRecorder(frame.Width, frame.Height, _session.Stats?.ConfiguredFps ?? 30);
            _recorder = recorder;
            await recorder.Ready;
            if (_quitting || (!_session.Scene.Sharing && !_session.CameraEnabled))
                recorder.Finish();
            else
                _session.Recorder = recorder;
            SetRecordButton(recording: true);
            RecordingResultPanel.Visibility = Visibility.Collapsed;
            _recordingBusy = false;
            _ = ObserveRecordingAsync(recorder);
            UpdateRecordingControls();
        }
        catch (Exception error)
        {
            if (recorder != null)
            {
                try { await recorder.Completion; } catch { }
            }
            _recorder = null;
            _recordingBusy = false;
            ShowRecordingResult("Couldn't start recording: " + error.Message, null);
            UpdateRecordingControls();
        }
    }

    private async Task ObserveRecordingAsync(VideoRecorder recorder)
    {
        try
        {
            await recorder.Completion;
            ShowRecordingResult("Saved " + Path.GetFileName(recorder.FilePath), recorder.FilePath);
        }
        catch (Exception error)
        {
            ShowRecordingResult("Recording stopped: " + error.Message, File.Exists(recorder.FilePath) ? recorder.FilePath : null);
        }
        finally
        {
            _session.Recorder = null;
            _recorder = null;
            _recordingBusy = false;
            SetRecordButton(recording: false);
            UpdateRecordingControls();
        }
    }

    private void OpenRecordings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(VideoRecorder.RecordingsDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(VideoRecorder.RecordingsDirectory) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            ShowRecordingResult("Couldn't open the recordings folder: " + error.Message, null);
        }
    }
}
