using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CamStudio.Desktop.Platform;

namespace CamStudio.Desktop;

/// <summary>Navigation, theme, settings-page actions and small helpers for the new layout.</summary>
public partial class MainWindow
{
    private bool _themeReady;
    private string? _lastRecordingPath;

    // ---------- theme ----------

    private void InitTheme()
    {
        var mode = ThemeManager.Parse(_settings.Theme);
        (mode switch
        {
            AppTheme.Light => ThemeLightRadio,
            AppTheme.Dark => ThemeDarkRadio,
            _ => ThemeSystemRadio,
        }).IsChecked = true;
        _themeReady = true;
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (!_themeReady)
            return;
        var mode = sender == ThemeLightRadio ? AppTheme.Light : sender == ThemeDarkRadio ? AppTheme.Dark : AppTheme.System;
        _settings.Theme = mode.ToString();
        _settings.Save();
        ThemeManager.Apply(mode);
    }

    // ---------- navigation ----------

    private void InitNavigation()
    {
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control)
                return;
            var target = e.Key switch
            {
                Key.D1 or Key.NumPad1 => NavLive,
                Key.D2 or Key.NumPad2 => NavEditor,
                Key.D3 or Key.NumPad3 => NavSettings,
                _ => null,
            };
            if (target != null)
            {
                target.IsChecked = true;
                e.Handled = true;
            }
        };
    }

    public void ShowPage(string page) =>
        (page.ToLowerInvariant() switch { "editor" => NavEditor, "settings" => NavSettings, _ => NavLive }).IsChecked = true;

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (LivePage == null)
            return; // still loading XAML
        LivePage.Visibility = sender == NavLive ? Visibility.Visible : Visibility.Collapsed;
        EditorHost.Visibility = sender == NavEditor ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = sender == NavSettings ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = sender == NavEditor ? "Editor" : sender == NavSettings ? "Settings" : "Live";
        if (sender == NavEditor)
            EnsureEditor();
    }

    private void Licenses_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-AUDIO-LICENSES.txt");
        if (File.Exists(path))
            OpenUrl(path);
    }

    private static void OpenUrl(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            App.Log(e);
        }
    }

    // ---------- beauty ----------

    private void BeautyAdjustToggle_Click(object sender, RoutedEventArgs e)
    {
        var show = BeautySlidersPanel.Visibility != Visibility.Visible;
        BeautySlidersPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BeautyAdjustToggle.Content = show ? "Hide adjustments" : "Show adjustments";
    }

    // ---------- recording ----------

    private void SetRecordButton(bool recording)
    {
        RecordLabel.Text = recording ? "Stop" : "Record";
        RecordGlyph.RadiusX = RecordGlyph.RadiusY = recording ? 2 : 5;
        RecordGlyph.Visibility = Visibility.Visible;
        RecordButton.Style = (Style)FindResource(recording ? "LiveStopAction" : "LiveAction");
        RecordGlyph.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Danger");
    }

    private void ShowRecordingResult(string message, string? file)
    {
        _lastRecordingPath = file;
        RecordingStatusText.Text = message;
        RecordingStatusText.ToolTip = file;
        EditRecordingButton.Visibility = file != null ? Visibility.Visible : Visibility.Collapsed;
        RecordingResultPanel.Visibility = Visibility.Visible;
    }

    private void EditRecording_Click(object sender, RoutedEventArgs e)
    {
        if (_lastRecordingPath == null || !File.Exists(_lastRecordingPath))
            return;
        NavEditor.IsChecked = true;
        OpenInEditor(_lastRecordingPath);
    }
}
