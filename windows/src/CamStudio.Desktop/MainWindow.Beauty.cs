using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CamStudio.Desktop.Core;

namespace CamStudio.Desktop;

public partial class MainWindow
{
    private readonly DispatcherTimer _beautyTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Dictionary<string, (Slider Slider, TextBlock Value)> _beautySliders = new();
    private JsonObject _beautyChanges = new();
    private bool _beautySync, _beautyReady, _beautyComparing;
    private string _beautyPending = "";
    private long _beautySentAt, _beautyCompareSentAt;

    private void InitBeauty()
    {
        foreach (var control in BeautySettings.Controls)
        {
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var number = new TextBlock { Text = "0", HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(number, Dock.Right);
            row.Children.Add(number);
            row.Children.Add(new TextBlock { Text = control.Label, FontSize = 12 });
            BeautySlidersPanel.Children.Add(row);
            var slider = new Slider
            {
                Minimum = control.Minimum, Maximum = control.Maximum, Value = 0,
                TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 10,
                // Clicking the track jumps straight to that value (default WPF steps by LargeChange).
                IsMoveToPointEnabled = true,
                MinHeight = 28, Margin = new Thickness(0, 2, 0, 0),
            };
            System.Windows.Automation.AutomationProperties.SetName(slider, control.Label);
            slider.ValueChanged += (_, _) =>
            {
                number.Text = ((int)Math.Round(slider.Value)).ToString();
                if (_beautySync || !_beautyReady) return;
                QueueBeautyChange(control.Key, JsonValue.Create((int)Math.Round(slider.Value))!);
            };
            _beautySliders.Add(control.Key, (slider, number));
            BeautySlidersPanel.Children.Add(slider);
        }
        _beautyReady = true;
        _beautyTimer.Tick += (_, _) => BeautyTick();
        _beautyTimer.Start();
        Deactivated += (_, _) => SetBeautyComparison(false);
        IsVisibleChanged += (_, _) => { if (!IsVisible) SetBeautyComparison(false); };
        UpdateBeauty();
    }

    private void QueueBeautyChange(string key, JsonNode value)
    {
        if (_session.Beauty == null) return;
        _beautyChanges[key] = value;
        BeautyStatusText.Text = "Applying on phone...";
    }

    private void TouchUp_Changed(object sender, RoutedEventArgs e)
    {
        if (!_beautyReady || _beautySync) return;
        TouchUpStrengthPanel.IsEnabled = TouchUpToggle.IsChecked == true;
        QueueTouchUp();
    }

    private void TouchUpStrength_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_beautyReady || _beautySync || TouchUpToggle.IsChecked != true) return;
        QueueTouchUp();
    }

    private void QueueTouchUp()
    {
        if (_session.Beauty == null) return;
        var on = TouchUpToggle.IsChecked == true;
        if (on) QueueBeautyChange("enabled", JsonValue.Create(true)!);
        QueueBeautyChange("skinSmooth", JsonValue.Create(on ? (int)Math.Round(TouchUpStrengthSlider.Value * 0.6) : 0)!);
        QueueBeautyChange("skinBrightness", JsonValue.Create(on ? (int)Math.Round(TouchUpStrengthSlider.Value * 0.1) : 0)!);
    }

    private void BeautyEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_beautyReady || _beautySync) return;
        var enabled = BeautyEnabledToggle.IsChecked == true;
        BeautySlidersPanel.IsEnabled = enabled;
        BeautyOriginalButton.IsEnabled = enabled;
        if (!enabled) SetBeautyComparison(false);
        QueueBeautyChange("enabled", JsonValue.Create(enabled)!);
    }

    private void ResetBeauty_Click(object sender, RoutedEventArgs e)
    {
        SetBeautyComparison(false);
        _beautySync = true;
        BeautyEnabledToggle.IsChecked = false;
        TouchUpToggle.IsChecked = false;
        TouchUpStrengthPanel.IsEnabled = false;
        foreach (var pair in _beautySliders.Values) pair.Slider.Value = 0;
        _beautySync = false;
        QueueBeautyChange("enabled", JsonValue.Create(false)!);
        foreach (var control in BeautySettings.Controls) QueueBeautyChange(control.Key, JsonValue.Create(0)!);
        BeautySlidersPanel.IsEnabled = false;
        BeautyOriginalButton.IsEnabled = false;
    }

    private void BeautyTick()
    {
        if (_quitting) return;
        var now = Environment.TickCount64;
        if (_session.Beauty != null && _beautyChanges.Count > 0)
        {
            var changes = _beautyChanges;
            _beautyChanges = new JsonObject();
            _beautyPending = Guid.NewGuid().ToString("N");
            _beautySentAt = now;
            _session.SetBeauty(changes, _beautyPending);
        }
        if (_beautyComparing && now - _beautyCompareSentAt >= 200)
        {
            _beautyCompareSentAt = now;
            _session.CompareBeauty(true);
        }
        UpdateBeauty();
    }

    private void UpdateBeauty()
    {
        if (!_beautyReady) return;
        var report = _session.Beauty;
        BeautyControlsPanel.IsEnabled = report != null && !_quitting;
        if (report == null)
        {
            BeautyStatusText.Text = _session.Phone == null
                ? "Connect a phone"
                : "Waiting for phone settings…";
            return;
        }
        if (_beautyChanges.Count > 0) return;
        if (_beautyPending.Length > 0 && report.Acknowledgement != _beautyPending && Environment.TickCount64 - _beautySentAt < 3000)
            return;
        var timedOut = _beautyPending.Length > 0 && report.Acknowledgement != _beautyPending;
        _beautyPending = "";
        _beautySync = true;
        try
        {
            BeautyEnabledToggle.IsChecked = report.Settings.Enabled;
            var smoothing = report.Settings.Values["skinSmooth"];
            TouchUpToggle.IsChecked = report.Settings.Enabled && smoothing > 0;
            TouchUpStrengthPanel.IsEnabled = TouchUpToggle.IsChecked == true;
            if (!TouchUpStrengthSlider.IsMouseCaptureWithin && smoothing > 0)
                TouchUpStrengthSlider.Value = Math.Clamp(smoothing / 0.6, 0, 100);
            BeautySlidersPanel.IsEnabled = report.Settings.Enabled;
            BeautyOriginalButton.IsEnabled = report.Settings.Enabled;
            foreach (var control in BeautySettings.Controls)
            {
                // Never move a slider out from under the user's mouse while it is being dragged.
                var slider = _beautySliders[control.Key].Slider;
                if (!slider.IsMouseCaptureWithin)
                    slider.Value = report.Settings.Values[control.Key];
            }
        }
        finally { _beautySync = false; }
        BeautyStatusText.Text = timedOut ? "Phone didn’t confirm. Showing its settings."
            : _beautyComparing || report.Original ? "Original · beauty temporarily bypassed"
            : report.Settings.Enabled ? report.Tracking : "";
    }

    private void ResetBeautySession()
    {
        SetBeautyComparison(false);
        _beautyChanges = new JsonObject();
        _beautyPending = "";
        UpdateBeauty();
    }

    private void SetBeautyComparison(bool original)
    {
        if (original == _beautyComparing) return;
        _beautyComparing = original;
        _beautyCompareSentAt = Environment.TickCount64;
        _session.CompareBeauty(original);
        if (!original && Mouse.Captured == BeautyOriginalButton) BeautyOriginalButton.ReleaseMouseCapture();
    }

    private void BeautyOriginal_Down(object sender, MouseButtonEventArgs e)
    {
        if (_session.Beauty == null) return;
        BeautyOriginalButton.CaptureMouse();
        SetBeautyComparison(true);
        e.Handled = true;
    }
    private void BeautyOriginal_Up(object sender, MouseButtonEventArgs e) { SetBeautyComparison(false); e.Handled = true; }
    private void BeautyOriginal_LostCapture(object sender, MouseEventArgs e) => SetBeautyComparison(false);
    private void BeautyOriginal_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => SetBeautyComparison(false);
    private void BeautyOriginal_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter) { SetBeautyComparison(true); e.Handled = true; }
    }
    private void BeautyOriginal_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter) { SetBeautyComparison(false); e.Handled = true; }
    }
}
