using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EditorHarness;

public partial class MainWindow : Window
{
    private bool _dark = true;

    public MainWindow()
    {
        InitializeComponent();
        CamStudio.Editor.EditorView.RecordingsFolder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "CamStudio Pro");
        // EditorHarness [--timeline] [--light] files… : import files (and put them on the timeline).
        var all = Environment.GetCommandLineArgs().Skip(1).ToList();
        var files = all.Where(File.Exists).ToList();
        var timeline = all.Contains("--timeline");
        if (all.Contains("--light")) { _dark = false; App.SetTheme(false); ThemeButton.Content = "Switch to dark"; }
        if (files.Count > 0)
            Loaded += (_, _) => Editor.ImportFiles(files, timeline);
        Closing += (_, e) => e.Cancel = !Editor.ConfirmDiscardChanges();
        // --shots <dir>: add a title, select every timeline item in turn (then nothing), render the window
        // to <dir>\sel-N-<kind>.png after each step, and close. No OS input is synthesised.
        var shotsAt = all.IndexOf("--shots");
        if (shotsAt >= 0 && shotsAt + 1 < all.Count)
            Loaded += async (_, _) => await ShotsAsync(all[shotsAt + 1]);
    }

    private async Task ShotsAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        await Task.Delay(5000); // import, thumbnails, waveforms
        Editor.AddTitleForTest();
        await Task.Delay(800);
        var count = Editor.SelectableCountForTest;
        for (var i = 0; i < count; i++)
        {
            var kind = Editor.SelectForTest(i);
            await Task.Delay(500);
            Save(Path.Combine(dir, $"sel-{i}-{kind}.png"));
        }
        Editor.SelectForTest(-1);
        await Task.Delay(500);
        Save(Path.Combine(dir, "sel-none.png"));
        Editor.AudioDemoForTest();
        await Task.Delay(1500);
        Save(Path.Combine(dir, "audio-demo.png"));
        await Task.Delay(6000); // let the cleaned preview render
        Save(Path.Combine(dir, "audio-demo-ready.png"));
        // Enhance voice card on an audio part, and the project panel's Sound section with a meter reading.
        Editor.VoiceDemoForTest();
        await Task.Delay(1500);
        Save(Path.Combine(dir, "voice-enhance.png"));
        Editor.SelectForTest(-1);
        Editor.SetLoudnessResultForTest("−21.3 LUFS · peak −6.2 dBTP · range 9.1 LU" + Environment.NewLine + "Export will set it to YouTube (−14 LUFS).");
        await Task.Delay(800);
        Save(Path.Combine(dir, "project-sound.png"));
        Editor.MarkCleanForTest();
        Close();
    }

    private void Save(string path)
    {
        var root = (FrameworkElement)Content;
        var dpi = VisualTreeHelper.GetDpi(this);
        var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        enc.Save(f);
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        _dark = !_dark;
        App.SetTheme(_dark);
        ThemeButton.Content = _dark ? "Switch to light" : "Switch to dark";
    }
}
