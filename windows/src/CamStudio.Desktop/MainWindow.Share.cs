using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using CamStudio.Desktop.Media;
using CamStudio.Desktop.Platform;

namespace CamStudio.Desktop;

/// <summary>
/// Screen share: the shared screen fills the picture and the phone camera sits in a round bubble at the
/// bottom right. Applies to the preview, recordings and the live stream alike, and can be switched on and
/// off at any time (the video size never changes).
/// </summary>
public partial class MainWindow
{
    private bool _shareBusy;
    private bool _initializingShare;

    private SceneCompositor Scene => _session.Scene;

    private void InitShare()
    {
        _initializingShare = true;
        var size = Enum.TryParse<SceneCompositor.BubbleSize>(_settings.BubbleSize, out var b) ? b : SceneCompositor.BubbleSize.Medium;
        Scene.Bubble = size;
        Scene.BubbleInPreview = _settings.BubbleInPreview;
        Scene.BubbleInRecordings = _settings.BubbleInRecordings;
        Scene.BubbleInStreams = _settings.BubbleInStreams;
        PreviewBubbleToggle.IsChecked = _settings.BubbleInPreview;
        RecordingBubbleToggle.IsChecked = _settings.BubbleInRecordings;
        StreamBubbleToggle.IsChecked = _settings.BubbleInStreams;
        (size switch
        {
            SceneCompositor.BubbleSize.Small => BubbleSmall,
            SceneCompositor.BubbleSize.Large => BubbleLarge,
            _ => BubbleMedium,
        }).IsChecked = true;
        if (!ScreenCapture.IsSupported)
        {
            ShareButton.IsEnabled = false;
            ShareIdlePanel.IsEnabled = false;
            ShareStatusText.Text = "Screen capture isn't available on this PC.";
        }
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                ShareToggle_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        };
        _initializingShare = false;
        UpdateShareControls();
    }

    private void ShareToggle_Click(object sender, RoutedEventArgs e)
    {
        if (Scene.Sharing)
            StopSharing();
        else
            SharePick_Click(sender, e);
    }

    private async void SharePick_Click(object sender, RoutedEventArgs e)
    {
        if (_shareBusy) return;
        _shareBusy = true;
        try
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            var capture = await ScreenCapture.PickAsync(hwnd);
            if (capture != null)
                StartSharing(capture);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            ShareStatusText.Text = "Couldn't start screen sharing: " + ex.Message;
        }
        finally
        {
            _shareBusy = false;
        }
    }

    private void ShareMonitor_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartSharing(ScreenCapture.ForPrimaryMonitor());
        }
        catch (Exception ex)
        {
            App.Log(ex);
            ShareStatusText.Text = "Couldn't share the screen: " + ex.Message;
        }
    }

    private void ShareStop_Click(object sender, RoutedEventArgs e) => StopSharing();

    private void StartSharing(ScreenCapture capture)
    {
        capture.Ended += () => Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(Scene.Screen, capture))
            {
                StopSharing();
                ShareStatusText.Text = "Sharing stopped: the window was closed.";
            }
        });
        Scene.Fps = _session.Stats?.ConfiguredFps is int f and > 0 ? f : 30;
        _hub.IgnorePause = true;
        Scene.SetScreen(capture); // replaces (and disposes) a previous source
        _previewVersion = -1;
        UpdateShareControls();
        UpdatePreviewState();
    }

    private void StopSharing()
    {
        Scene.SetScreen(null);
        _hub.IgnorePause = false;
        _previewVersion = -1;
        UpdateShareControls();
        UpdatePreviewState();
    }

    private void Bubble_Checked(object sender, RoutedEventArgs e)
    {
        var size = sender == BubbleSmall ? SceneCompositor.BubbleSize.Small
            : sender == BubbleLarge ? SceneCompositor.BubbleSize.Large : SceneCompositor.BubbleSize.Medium;
        Scene.Bubble = size;
        if (_settings.BubbleSize != size.ToString())
        {
            _settings.BubbleSize = size.ToString();
            _settings.Save();
        }
    }

    private void UpdateShareControls()
    {
        UpdateCameraControls();
    }

    private void BubbleVisibility_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializingShare || PreviewBubbleToggle == null || RecordingBubbleToggle == null || StreamBubbleToggle == null) return;
        Scene.BubbleInPreview = _settings.BubbleInPreview = PreviewBubbleToggle.IsChecked == true;
        Scene.BubbleInRecordings = _settings.BubbleInRecordings = RecordingBubbleToggle.IsChecked == true;
        Scene.BubbleInStreams = _settings.BubbleInStreams = StreamBubbleToggle.IsChecked == true;
        _settings.Save();
        UpdateShareControls();
    }

    private void CameraToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_session.CameraRequested && !Scene.Sharing) _recorder?.Finish();
        _session.SetCameraEnabled(!_session.CameraRequested);
        UpdateCameraControls();
    }

    private void UpdateCameraControls()
    {
        CameraToggleButton.IsEnabled = _session.Phone != null;
        CameraToggleButton.Content = _session.CameraRequested ? "Close camera" : "Open camera";
        CameraToggleButton.ToolTip = _session.CameraRequested
            ? "Release the phone camera, microphone and video processing. Screen sharing continues."
            : "Start phone camera capture. Connecting alone keeps it off.";
        UpdateShareControlsCore();
    }

    private void UpdateShareControlsCore()
    {
        var sharing = Scene.Sharing;
        ShareButtonLabel.Text = sharing ? "Stop sharing" : "Share screen";
        ShareButton.Style = (Style)FindResource(sharing ? "LiveStopAction" : "LiveAction");
        ShareIdlePanel.Visibility = sharing ? Visibility.Collapsed : Visibility.Visible;
        ShareActivePanel.Visibility = sharing ? Visibility.Visible : Visibility.Collapsed;
        ShareBadge.Visibility = sharing ? Visibility.Visible : Visibility.Collapsed;
        ShareDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, sharing ? "Good" : "TextDim");
        if (sharing)
        {
            ShareStatusText.Text = $"Sharing “{Scene.Screen!.Name}”";
        }
        else if (ScreenCapture.IsSupported && !ShareStatusText.Text.StartsWith("Sharing stopped") && !ShareStatusText.Text.StartsWith("Couldn't"))
        {
            ShareStatusText.Text = "";
        }
    }
}
