using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CamStudio.Editor.Engine;
using CamStudio.Editor.Model;
using Microsoft.Win32;

namespace CamStudio.Editor;

/// <summary>
/// Timeline video editor: media bin, preview, properties and a multi-track timeline. Rendering and
/// export go through ffmpeg (see <see cref="ExportBuilder"/>); live preview uses MediaElement.
/// </summary>
public partial class EditorView : UserControl
{
    /// <summary>Folder offered by "Add recordings" and used as the default export location.</summary>
    public static string? RecordingsFolder { get; set; }

    private EditorProject _project = new();
    private readonly History _history = new();
    private readonly ObservableCollection<BinItem> _bin = new();
    private readonly Dictionary<string, ImageSource> _thumbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BitmapImage> _stills = new(StringComparer.OrdinalIgnoreCase);
    private string? _projectPath;
    private bool _dirty;
    private object? _selected;

    // playback
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
    private readonly Stopwatch _clock = new();
    private double _clockBase;
    private double _playhead;
    private bool _playing;
    private string? _playerPath;
    private bool _playerOpen, _playerRunning;
    private Guid _playerClip;
    private string? _musicPath;
    private bool _musicOpen, _musicRunning;
    private Guid _musicClip;
    private string _titleSignature = "";

    // exact colour frame while paused
    private readonly DispatcherTimer _exactTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private CancellationTokenSource? _exactCts;
    private string _exactKey = "";

    // export
    private CancellationTokenSource? _exportCts;
    private string? _exportedFile;

    private Point _binDownAt;
    private BinItem? _binDownItem;

    public bool HasUnsavedChanges => _dirty;

    public EditorView()
    {
        InitializeComponent();
        BinList.ItemsSource = _bin;
        Timeline.Project = _project;
        Timeline.Thumbnail = path => _thumbs.TryGetValue(path, out var img) ? img : null;
        Timeline.Waveform = Waveforms.Get;
        Timeline.IsCleaned = c => c.HasCleanup();
        Timeline.SelectedRowChanged += HighlightTrackHeader;
        Waveforms.Loaded += _ => Dispatcher.BeginInvoke(() => Timeline.InvalidateVisual());
        AudioProxy.Changed += () => Dispatcher.BeginInvoke(OnProxyChanged);
        TimelineScroll.ScrollChanged += (_, e) => { if (e.HorizontalChange != 0 || e.ViewportWidthChange != 0) Timeline.InvalidateVisual(); };
        Timeline.SeekRequested += t => Seek(t);
        Timeline.SelectionChanged += clip => Select(clip, fromTimeline: true);
        Timeline.EditStarting += key => { _history.Record(_project, null); MarkDirty(); };
        Timeline.EditCompleted += () =>
        {
            _project.Layout();
            BuildProperties();
            RefreshAll();
        };
        _tick.Tick += (_, _) => OnTick();
        _exactTimer.Tick += (_, _) => { _exactTimer.Stop(); _ = RenderExactFrameAsync(); };
        BuildTrackHeaders();
        FFmpeg.Locate();
        Loaded += OnLoaded;
        Unloaded += (_, _) => Pause();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) Pause();
            else Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (!IsKeyboardFocusWithin) Focus(); });
        };
        PreviewKeyDown += OnPreviewKeyDown;
        Drop += OnFileDrop;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        ApplyZoom();
        RefreshAll();
        BuildProperties();
    }

    private bool _initialized;
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!IsKeyboardFocusWithin) Focus();
        if (_initialized) return;
        _initialized = true;
        if (FFmpeg.IsAvailable || FFmpeg.Locate())
        {
            SetupOverlay.Visibility = Visibility.Collapsed;
            _ = FFmpeg.WorkingEncodersAsync(); // warm up the encoder check
        }
        else
        {
            SetupOverlay.Visibility = Visibility.Visible;
        }
    }

    // ================================================================ helpers

    public static string FormatShort(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private string Timecode(double seconds)
    {
        var fps = Math.Max(1, (int)Math.Round(_project.Fps));
        var total = (long)Math.Floor(Math.Max(0, seconds) * fps + 1e-6);
        var frames = total % fps;
        var s = total / fps;
        return $"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00}:{frames:00}";
    }

    private void ShowStatus(string? message)
    {
        StatusText.Text = message ?? "";
        StatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void MarkDirty()
    {
        _dirty = true;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var name = _projectPath != null ? Path.GetFileNameWithoutExtension(_projectPath) : _project.Name;
        ProjectNameText.Text = name + (_dirty ? " · edited" : "");
    }

    /// <summary>One undoable change. Sliders pass a key so a drag becomes a single undo step.</summary>
    private void Mutate(Action change, string? key = null, bool rebuildProperties = false)
    {
        _loudnessResult = null; // the mix changed: an earlier measurement no longer applies
        _history.Record(_project, key);
        change();
        _project.Layout();
        MarkDirty();
        if (rebuildProperties) BuildProperties();
        RefreshAll();
    }

    private void RefreshAll()
    {
        _project.Layout();
        Timeline.Project = _project;
        Timeline.Selected = _selected;
        Timeline.InvalidateMeasure();
        Timeline.InvalidateVisual();
        Stage.Width = Math.Max(16, _project.Width);
        Stage.Height = Math.Max(16, _project.Height);
        DurationText.Text = Timecode(_project.PlayLength);
        PreviewEmpty.Visibility = _project.Video.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var audioOnly = _project.Video.Count == 0 && _project.Music.Count > 0;
        PreviewEmptyIcon.Text = audioOnly ? "" : "";
        PreviewEmptyText.Text = audioOnly ? "Audio project · press Space to listen" : "Add clips to the timeline to start editing";
        BinEmpty.Visibility = _bin.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UndoButton.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = _history.CanRedo;
        DeleteButton.IsEnabled = _selected != null;
        ExportButton.IsEnabled = _project.CanExport;
        RequestProxies();
        if (_playhead > Math.Max(_project.Extent, 0)) _playhead = Math.Max(0, _project.Extent);
        _titleSignature = "";
        UpdateTitle();
        UpdatePreview(seek: true);
    }

    // ================================================================ project files

    private void New_Click(object sender, RoutedEventArgs e) => NewProject();
    private void Open_Click(object sender, RoutedEventArgs e) => OpenProject();
    private void Save_Click(object sender, RoutedEventArgs e) => SaveProject(saveAs: false);

    private void NewProject()
    {
        if (!ConfirmDiscardChanges()) return;
        Pause();
        _project = new EditorProject();
        _projectPath = null;
        _history.Clear();
        _bin.Clear();
        _selected = null;
        _dirty = false;
        _playhead = 0;
        ResetPlayers();
        BuildProperties();
        RefreshAll();
    }

    private void OpenProject()
    {
        if (!ConfirmDiscardChanges()) return;
        var dialog = new OpenFileDialog { Filter = "CamStudio project (*.camproj)|*.camproj", Title = "Open project" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            Pause();
            _project = EditorProject.Load(dialog.FileName);
            _projectPath = dialog.FileName;
            _history.Clear();
            _selected = null;
            _dirty = false;
            _playhead = 0;
            ResetPlayers();
            RebuildBin();
            BuildProperties();
            RefreshAll();
            var missing = _project.Assets.Count(a => !File.Exists(a.Path));
            ShowStatus(missing > 0 ? $"{missing} media file{(missing == 1 ? " is" : "s are")} missing." : null);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, "Couldn't open the project: " + ex.Message, "CamStudio Pro", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool SaveProject(bool saveAs)
    {
        var path = _projectPath;
        if (path == null || saveAs)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "CamStudio project (*.camproj)|*.camproj",
                FileName = _project.Name + ".camproj",
                InitialDirectory = DefaultFolder(),
                Title = "Save project",
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return false;
            path = dialog.FileName;
        }
        try
        {
            _project.Name = Path.GetFileNameWithoutExtension(path);
            _project.Save(path);
            _projectPath = path;
            _dirty = false;
            UpdateTitle();
            ShowStatus(null);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, "Couldn't save the project: " + ex.Message, "CamStudio Pro", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>Asks to save unsaved changes. Returns false if the user cancelled.</summary>
    public bool ConfirmDiscardChanges()
    {
        if (!_dirty) return true;
        var name = _projectPath != null ? Path.GetFileNameWithoutExtension(_projectPath) : _project.Name;
        var owner = Window.GetWindow(this);
        var answer = owner != null
            ? MessageBox.Show(owner, $"Save changes to “{name}”?", "CamStudio Pro", MessageBoxButton.YesNoCancel, MessageBoxImage.Question)
            : MessageBox.Show($"Save changes to “{name}”?", "CamStudio Pro", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => SaveProject(saveAs: false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private static string DefaultFolder()
    {
        var folder = RecordingsFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "CamStudio Pro");
        try { Directory.CreateDirectory(folder); } catch { }
        return folder;
    }

    // ================================================================ media bin

    private void Import_Click(object sender, RoutedEventArgs e) => PickAndImport();

    private void PickAndImport()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Import media",
            Filter = "Media|" + string.Join(";", FFmpeg.MediaExtensions.Concat(FFmpeg.ImageExtensions).Select(x => "*" + x)) + "|All files|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ImportFiles(dialog.FileNames);
    }

    private void AddRecordings_Click(object sender, RoutedEventArgs e)
    {
        var folder = DefaultFolder();
        var files = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.mp4").OrderByDescending(File.GetLastWriteTimeUtc).ToList()
            : new List<string>();
        if (files.Count == 0)
        {
            ShowStatus("No recordings yet. Record from the Live page first.");
            return;
        }
        ImportFiles(files);
    }

    /// <summary>Adds files to the media bin (probing them with ffprobe).</summary>
    public void ImportFiles(IEnumerable<string> paths) => _ = ImportAsync(paths, null);

    /// <summary>Imports files and optionally appends them to the timeline (e.g. "Edit this recording").</summary>
    public void ImportFiles(IEnumerable<string> paths, bool addToTimeline) =>
        _ = ImportAsync(paths, addToTimeline ? added =>
        {
            foreach (var a in added) AppendToTimeline(a);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ZoomToFit);
        } : null);

    private async Task<List<MediaAsset>> ImportAsync(IEnumerable<string> paths, Action<List<MediaAsset>>? then)
    {
        var added = new List<MediaAsset>();
        if (!FFmpeg.IsAvailable && !FFmpeg.Locate())
        {
            SetupOverlay.Visibility = Visibility.Visible;
            return added;
        }
        var failed = new List<string>();
        var list = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return added;
        ShowStatus(list.Count == 1 ? "Importing…" : $"Importing {list.Count} files…");
        foreach (var path in list)
        {
            var existing = _project.Asset(path);
            if (existing != null)
            {
                added.Add(existing);
                continue;
            }
            if (!FFmpeg.IsSupportedFile(path))
            {
                failed.Add(Path.GetFileName(path));
                continue;
            }
            var asset = await FFmpeg.ProbeAsync(path);
            if (asset == null)
            {
                failed.Add(Path.GetFileName(path));
                continue;
            }
            _project.Assets.Add(asset);
            AddBinItem(asset);
            added.Add(asset);
            MarkDirty();
        }
        ShowStatus(failed.Count == 0 ? null : $"Couldn't import {string.Join(", ", failed.Take(3))}{(failed.Count > 3 ? "…" : "")}");
        RefreshAll();
        then?.Invoke(added);
        return added;
    }

    private void RebuildBin()
    {
        _bin.Clear();
        foreach (var asset in _project.Assets)
            AddBinItem(asset);
    }

    private void AddBinItem(MediaAsset asset)
    {
        var item = new BinItem(asset);
        _bin.Add(item);
        if (_thumbs.TryGetValue(asset.Path, out var cached))
        {
            item.Thumbnail = cached;
            return;
        }
        if (asset.IsAudioOnly || !File.Exists(asset.Path)) return;
        _ = LoadThumbnailAsync(item);
    }

    private async Task LoadThumbnailAsync(BinItem item)
    {
        var bmp = await FFmpeg.ThumbnailAsync(item.Asset);
        if (bmp == null) return;
        _thumbs[item.Path] = bmp;
        item.Thumbnail = bmp;
        Timeline.InvalidateVisual();
    }

    private void Bin_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _binDownAt = e.GetPosition(BinList);
        _binDownItem = (e.OriginalSource as FrameworkElement)?.DataContext as BinItem;
    }

    private void Bin_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _binDownItem == null) return;
        var p = e.GetPosition(BinList);
        if (Math.Abs(p.X - _binDownAt.X) < 6 && Math.Abs(p.Y - _binDownAt.Y) < 6) return;
        var item = _binDownItem;
        _binDownItem = null;
        DragDrop.DoDragDrop(BinList, new DataObject("CamStudioAsset", item.Path), DragDropEffects.Copy);
        Timeline.DropTime = null;
    }

    private void Bin_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is BinItem item)
            AppendToTimeline(item.Asset);
    }

    private void Bin_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && BinList.SelectedItem is BinItem item)
        {
            AppendToTimeline(item.Asset);
            e.Handled = true;
        }
    }

    private void BinAdd_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BinItem item)
            AppendToTimeline(item.Asset);
    }

    private void AppendToTimeline(MediaAsset asset)
    {
        if (asset.IsAudioOnly)
        {
            var start = _project.Music.Count == 0 ? 0 : _project.Music.Max(m => m.End);
            AddMusic(asset, start);
            return;
        }
        InsertVideo(asset, _project.Video.Count);
    }

    private void InsertVideo(MediaAsset asset, int index)
    {
        var first = _project.Video.Count == 0;
        var clip = _project.CreateVideoClip(asset);
        Mutate(() =>
        {
            if (first && asset.HasVideo && !asset.IsImage && asset.Width > 0 && asset.Height > 0)
            {
                // The first clip sets the project shape (16:9, vertical, …) and frame rate.
                _project.Width = asset.Width % 2 == 0 ? asset.Width : asset.Width + 1;
                _project.Height = asset.Height % 2 == 0 ? asset.Height : asset.Height + 1;
                if (asset.Fps is > 10 and <= 120) _project.Fps = Math.Round(asset.Fps, 3);
            }
            _project.Video.Insert(Math.Clamp(index, 0, _project.Video.Count), clip);
        });
        Select(clip);
        if (first) ZoomToFit();
    }

    private void AddMusic(MediaAsset asset, double start)
    {
        var clip = _project.CreateMusicClip(asset, start);
        Mutate(() => _project.Music.Add(clip));
        Select(clip);
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var expanded = files.SelectMany(f => Directory.Exists(f) ? Directory.EnumerateFiles(f) : new[] { f });
            ImportFiles(expanded);
            e.Handled = true;
        }
    }

    // ================================================================ timeline drop / zoom

    private void Timeline_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("CamStudioAsset") || e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            Timeline.DropTime = Timeline.TimeAtX(e.GetPosition(Timeline).X);
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Timeline_DragLeave(object sender, DragEventArgs e) => Timeline.DropTime = null;

    private void Timeline_Drop(object sender, DragEventArgs e)
    {
        var t = Timeline.TimeAtX(e.GetPosition(Timeline).X);
        Timeline.DropTime = null;
        e.Handled = true;
        if (e.Data.GetData("CamStudioAsset") is string path && _project.Asset(path) is { } asset)
        {
            DropAsset(asset, t);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            _ = ImportAsync(files, added =>
            {
                foreach (var a in added)
                {
                    DropAsset(a, t);
                    if (!a.IsAudioOnly) t = _project.Video.FirstOrDefault(v => v.Path == a.Path)?.End ?? t;
                }
            });
        }
    }

    private void DropAsset(MediaAsset asset, double t)
    {
        if (asset.IsAudioOnly)
        {
            AddMusic(asset, t);
            return;
        }
        var index = _project.Video.Count;
        for (var i = 0; i < _project.Video.Count; i++)
            if (t < _project.Video[i].Start + _project.Video[i].Duration / 2) { index = i; break; }
        InsertVideo(asset, index);
    }

    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyZoom();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomSlider.Value = Math.Min(ZoomSlider.Maximum, ZoomSlider.Value + 0.5);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomSlider.Value = Math.Max(ZoomSlider.Minimum, ZoomSlider.Value - 0.5);
    private void ZoomFit_Click(object sender, RoutedEventArgs e) => ZoomToFit();

    private void ApplyZoom()
    {
        if (Timeline == null) return;
        // 0..7 → 5..640 px per second (log scale)
        var center = TimelineScroll.ViewportWidth > 0 ? Timeline.TimeAtX(TimelineScroll.HorizontalOffset + TimelineScroll.ViewportWidth / 2) : 0;
        Timeline.PixelsPerSecond = 5 * Math.Pow(2, ZoomSlider.Value);
        Timeline.UpdateLayout();
        if (TimelineScroll.ViewportWidth > 0)
            TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, Timeline.XAt(center) - TimelineScroll.ViewportWidth / 2));
    }

    private void ZoomToFit()
    {
        var extent = Math.Max(5, _project.Extent);
        var width = Math.Max(200, TimelineScroll.ViewportWidth - 60);
        var pps = width / extent;
        ZoomSlider.Value = Math.Clamp(Math.Log2(pps / 5), ZoomSlider.Minimum, ZoomSlider.Maximum);
        TimelineScroll.ScrollToHorizontalOffset(0);
    }

    private void TimelineScroll_SizeChanged(object sender, SizeChangedEventArgs e) => Timeline.InvalidateMeasure();

    private void Timeline_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            ZoomSlider.Value = Math.Clamp(ZoomSlider.Value + (e.Delta > 0 ? 0.25 : -0.25), ZoomSlider.Minimum, ZoomSlider.Maximum);
        else
            TimelineScroll.ScrollToHorizontalOffset(TimelineScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private readonly Dictionary<TimelineControl.Row, Border> _trackHeaders = new();

    /// <summary>Highlights the track header of the row that holds the selection.</summary>
    private void HighlightTrackHeader(TimelineControl.Row row)
    {
        foreach (var (r, border) in _trackHeaders)
        {
            if (r == row) border.SetResourceReference(Border.BackgroundProperty, "Selected");
            else border.Background = Brushes.Transparent;
            if (border.Child is StackPanel sp && sp.Children.Count == 2)
            {
                ((TextBlock)sp.Children[0]).SetResourceReference(TextBlock.ForegroundProperty, r == row ? "AccentText" : "TextDim");
                ((TextBlock)sp.Children[1]).SetResourceReference(TextBlock.ForegroundProperty, r == row ? "Text" : "TextSoft");
            }
        }
    }

    private void BuildTrackHeaders()
    {
        TrackHeaders.Children.Clear();
        _trackHeaders.Clear();
        void Add(TimelineControl.Row key, double y, double h, string glyph, string label)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Width = 72, Height = h };
            var icon = new TextBlock { Text = glyph, FontSize = 12, Margin = new Thickness(14, 0, 6, 0) };
            icon.SetResourceReference(StyleProperty, "Icon");
            icon.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
            var text = new TextBlock { Text = label, FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextSoft");
            row.Children.Add(icon);
            row.Children.Add(text);
            var holder = new Border { Child = row, Width = 72, Height = h, Background = Brushes.Transparent };
            Canvas.SetTop(holder, y);
            TrackHeaders.Children.Add(holder);
            _trackHeaders[key] = holder;
        }
        Add(TimelineControl.Row.Titles, TimelineControl.TitleY, TimelineControl.TitleH, "\uE8D2", "Titles");
        Add(TimelineControl.Row.Video, TimelineControl.VideoY, TimelineControl.VideoH, "\uE714", "Video");
        Add(TimelineControl.Row.Audio, TimelineControl.AudioY, TimelineControl.AudioH, "\uE767", "Audio");
        Add(TimelineControl.Row.Music, TimelineControl.MusicY, TimelineControl.MusicH, "\uEC4F", "Music");
    }

    // ================================================================ editing commands

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();
    private void Split_Click(object sender, RoutedEventArgs e) => Split();
    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();
    private void AddTitle_Click(object sender, RoutedEventArgs e) => AddTitle();

    private void Undo() => Restore(_history.Undo(_project));
    private void Redo() => Restore(_history.Redo(_project));

    private void Restore(EditorProject? project)
    {
        if (project == null) return;
        var selectedId = SelectedId();
        _project = project;
        MarkDirty();
        if (_bin.Count != _project.Assets.Count) RebuildBin();
        _selected = FindById(selectedId);
        BuildProperties();
        RefreshAll();
    }

    /// <summary>Selection identity that survives undo/redo (which replace every object).</summary>
    private (Guid Id, bool Audio)? SelectedId() => _selected switch
    {
        VideoClip v => (v.Id, false),
        AudioPart a => (a.Clip.Id, true),
        MusicClip m => (m.Id, false),
        TitleClip t => (t.Id, false),
        _ => null,
    };

    private object? FindById((Guid Id, bool Audio)? key)
    {
        if (key is not { } k) return null;
        if (_project.Video.FirstOrDefault(v => v.Id == k.Id) is { } video)
            return k.Audio && video.HasAudio && !video.IsImage ? new AudioPart(video) : video;
        return (object?)_project.Music.FirstOrDefault(m => m.Id == k.Id) ?? _project.Titles.FirstOrDefault(t => t.Id == k.Id);
    }

    private void Split()
    {
        var t = _playhead;
        const double margin = 0.05;
        // Prefer the selected clip if the playhead is inside it, otherwise the video clip under the playhead.
        object? target = _selected switch
        {
            VideoClip v when t > v.Start + margin && t < v.End - margin => v,
            AudioPart a when t > a.Clip.Start + margin && t < a.Clip.End - margin => a.Clip,
            MusicClip m when t > m.Start + margin && t < m.End - margin => m,
            TitleClip ti when t > ti.Start + margin && t < ti.End - margin => ti,
            _ => null,
        };
        target ??= _project.Video.FirstOrDefault(v => t > v.Start + margin && t < v.End - margin);
        target ??= _project.Music.LastOrDefault(m => t > m.Start + margin && t < m.End - margin);
        if (target == null)
        {
            ShowStatus("Move the playhead inside a clip to split it.");
            return;
        }
        var wasAudio = _selected is AudioPart;
        object? right = null;
        Mutate(() => right = _project.SplitAt(target, t, margin));
        Select(wasAudio && right is VideoClip rv ? new AudioPart(rv) : right);
        ShowStatus(null);
    }

    private static T Clone<T>(T value) => EditorProject.Clone(value);

    private void DeleteSelected()
    {
        var target = _selected;
        if (target == null) return;
        if (target is AudioPart part)
        {
            // Deleting the audio part removes that clip's sound; the video stays.
            if (part.Clip.Muted) return;
            Mutate(() => part.Clip.Muted = true, rebuildProperties: true);
            ShowStatus("Audio removed from this clip. Split first to remove just part of it.");
            return;
        }
        Mutate(() =>
        {
            switch (target)
            {
                case VideoClip v: _project.Video.Remove(v); break;
                case MusicClip m: _project.Music.Remove(m); break;
                case TitleClip t: _project.Titles.Remove(t); break;
            }
        });
        Select(null);
    }

    /// <summary>Everything selectable, in timeline order (Tab / Shift+Tab cycle through it).</summary>
    private List<object> SelectableItems()
    {
        var items = new List<(double Start, int Lane, object Item)>();
        foreach (var t in _project.Titles) items.Add((t.Start, 0, t));
        foreach (var v in _project.Video)
        {
            items.Add((v.Start, 1, v));
            if (v.HasAudio && !v.IsImage) items.Add((v.Start, 2, new AudioPart(v)));
        }
        foreach (var m in _project.Music) items.Add((m.Start, 3, m));
        return items.OrderBy(i => i.Start).ThenBy(i => i.Lane).Select(i => i.Item).ToList();
    }

    // ---- test hooks (EditorHarness --shots) ----
    internal int SelectableCountForTest => SelectableItems().Count;
    /// <summary>Selects the i-th selectable item (timeline order) or nothing for -1; returns its kind.</summary>
    internal string SelectForTest(int index)
    {
        var items = SelectableItems();
        var item = index >= 0 && index < items.Count ? items[index] : null;
        Select(item);
        return item?.GetType().Name ?? "none";
    }
    internal void AddTitleForTest() => AddTitle();
    internal void MarkCleanForTest() => _dirty = false;
    /// <summary>Turns on clean-up for the first clip's audio, removes the second clip's audio, selects the first audio part.</summary>
    internal void AudioDemoForTest()
    {
        var withAudio = _project.Video.Where(v => v.HasAudio && !v.IsImage).ToList();
        if (withAudio.Count == 0) return;
        Mutate(() =>
        {
            withAudio[0].NoiseReduction = NoiseLevel.Medium;
            withAudio[0].RemoveHum = true;
            if (withAudio.Count > 1) withAudio[1].Muted = true;
            foreach (var m in _project.Music) m.Normalize = true;
        });
        Select(new AudioPart(withAudio[0]));
    }

    private void CycleSelection(bool back)
    {
        var items = SelectableItems();
        if (items.Count == 0) return;
        var index = items.FindIndex(i => Equals(i, _selected));
        index = index < 0 ? (back ? items.Count - 1 : 0) : (index + (back ? -1 : 1) + items.Count) % items.Count;
        Select(items[index]);
        // keep it in view
        var start = items[index] switch { VideoClip v => v.Start, AudioPart a => a.Clip.Start, MusicClip m => m.Start, TitleClip t => t.Start, _ => 0 };
        var x = Timeline.XAt(start);
        if (x < TimelineScroll.HorizontalOffset || x > TimelineScroll.HorizontalOffset + TimelineScroll.ViewportWidth - 40)
            TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - 80));
    }

    private void AddTitle()
    {
        var start = _playhead;
        var limit = Math.Max(_project.Duration, start + 3);
        var title = new TitleClip { Start = start, Duration = Math.Min(3, Math.Max(0.5, limit - start)), Text = "Your title" };
        Mutate(() => _project.Titles.Add(title));
        Select(title);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _titleBox?.Focus());
        _titleBox?.SelectAll();
    }

    private void Select(object? clip, bool fromTimeline = false)
    {
        _selected = clip;
        if (!fromTimeline) Timeline.Selected = clip;
        DeleteButton.IsEnabled = clip != null;
        BuildProperties();
    }

    // ================================================================ keyboard

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ExportOverlay.Visibility == Visibility.Visible || SetupOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape && ExportOverlay.Visibility == Visibility.Visible && _exportCts == null)
            {
                ExportOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            return;
        }
        if (_holdButton != null && Keyboard.FocusedElement == _holdButton && e.Key is Key.Space or Key.Enter)
            return; // "Hold to hear original" handles these itself
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var typing = Keyboard.FocusedElement is TextBox;

        if (ctrl)
        {
            switch (key)
            {
                case Key.Z when shift: Redo(); e.Handled = true; return;
                case Key.Z when !typing: Undo(); e.Handled = true; return;
                case Key.Y when !typing: Redo(); e.Handled = true; return;
                case Key.S: SaveProject(saveAs: shift); e.Handled = true; return;
                case Key.O: OpenProject(); e.Handled = true; return;
                case Key.N: NewProject(); e.Handled = true; return;
                case Key.I: PickAndImport(); e.Handled = true; return;
                case Key.E: Export_Click(this, new RoutedEventArgs()); e.Handled = true; return;
            }
            return;
        }
        if (typing || Keyboard.FocusedElement is ComboBox) return;

        // Tab cycles the timeline selection while the timeline (or the editor itself) has focus.
        if (key == Key.Tab && (Keyboard.FocusedElement is TimelineControl || ReferenceEquals(Keyboard.FocusedElement, this)))
        {
            CycleSelection(back: shift);
            e.Handled = true;
            return;
        }

        var frame = 1.0 / Math.Max(1, _project.Fps);
        switch (key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.S: Split(); break;
            case Key.T: AddTitle(); break;
            case Key.Delete:
            case Key.Back: DeleteSelected(); break;
            case Key.Left: Pause(); Seek(_playhead - (shift ? 1 : frame)); break;
            case Key.Right: Pause(); Seek(_playhead + (shift ? 1 : frame)); break;
            case Key.Home: Seek(0); break;
            case Key.End: Seek(_project.PlayLength); break;
            case Key.OemPlus:
            case Key.Add: ZoomIn_Click(this, e); break;
            case Key.OemMinus:
            case Key.Subtract: ZoomOut_Click(this, e); break;
            case Key.Escape: Select(null); Timeline.Selected = null; break;
            default: return;
        }
        e.Handled = true;
    }

    // ================================================================ playback

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();
    private void PrevFrame_Click(object sender, RoutedEventArgs e) { Pause(); Seek(_playhead - 1.0 / Math.Max(1, _project.Fps)); }
    private void NextFrame_Click(object sender, RoutedEventArgs e) { Pause(); Seek(_playhead + 1.0 / Math.Max(1, _project.Fps)); }
    private void Preview_MouseDown(object sender, MouseButtonEventArgs e) { Focus(); if (e.ClickCount == 1) TogglePlay(); }

    private void TogglePlay()
    {
        if (_playing) Pause();
        else Play();
    }

    private void Play()
    {
        if (_project.PlayLength <= 0) return;
        if (_playhead >= _project.PlayLength - 0.02) _playhead = 0;
        _playing = true;
        _clockBase = _playhead;
        _clock.Restart();
        _tick.Start();
        PlayGlyph.Text = "";
        System.Windows.Automation.AutomationProperties.SetName(PlayButton, "Pause");
        HideExactFrame();
        UpdatePreview(seek: true);
    }

    private void Pause()
    {
        if (!_playing) return;
        _playing = false;
        _tick.Stop();
        _clock.Stop();
        PlayGlyph.Text = "";
        System.Windows.Automation.AutomationProperties.SetName(PlayButton, "Play");
        UpdatePreview(seek: true);
    }

    private void Seek(double t)
    {
        _playhead = Math.Clamp(t, 0, Math.Max(_project.Extent, 0));
        if (_playing)
        {
            _clockBase = _playhead;
            _clock.Restart();
        }
        UpdatePreview(seek: true);
    }

    private void OnTick()
    {
        if (!_playing) return;
        _playhead = _clockBase + _clock.Elapsed.TotalSeconds;
        if (_playhead >= _project.PlayLength)
        {
            _playhead = _project.PlayLength;
            Pause();
            return;
        }
        UpdatePreview(seek: false);
        // keep the playhead in view
        var x = Timeline.XAt(_playhead);
        if (x < TimelineScroll.HorizontalOffset || x > TimelineScroll.HorizontalOffset + TimelineScroll.ViewportWidth - 40)
            TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - 80));
    }

    private void ResetPlayers()
    {
        Player.Stop();
        Player.Source = null;
        _playerPath = null;
        _playerOpen = _playerRunning = false;
        MusicPlayer.Stop();
        MusicPlayer.Source = null;
        _musicPath = null;
        _musicOpen = _musicRunning = false;
        CleanPlayer.Stop();
        CleanPlayer.Source = null;
        _cleanPath = null;
        _cleanOpen = _cleanRunning = false;
        HideExactFrame();
    }

    // ---------------------------------------------------------------- cleaned-audio preview

    private string? _cleanPath;
    private bool _cleanOpen, _cleanRunning;
    private long _cleanSyncAt;

    private void Clean_MediaOpened(object sender, RoutedEventArgs e)
    {
        _cleanOpen = true;
        _cleanRunning = false;
        UpdatePreview(seek: true);
    }

    /// <summary>Starts rendering cleaned-audio previews for every clip that has clean-up turned on.</summary>
    private void RequestProxies()
    {
        foreach (var v in _project.Video.Where(v => v.PlaysAudio && v.HasCleanup())) AudioProxy.Get(v);
        foreach (var m in _project.Music.Where(m => m.PlaysAudio && m.HasCleanup())) AudioProxy.Get(m);
        UpdateCleaningStatus();
    }

    private void OnProxyChanged()
    {
        UpdateCleaningStatus();
        UpdatePreview(seek: true);
    }

    private TextBlock? _cleanStatusText;
    private const string CleaningMessage = "Cleaning audio for the preview…";

    private void UpdateCleaningStatus()
    {
        var pending = _project.Video.Cast<IAudioClip>().Concat(_project.Music).Any(c => c.HasCleanup() && AudioProxy.IsPending(c));
        if (pending) ShowStatus(CleaningMessage);
        else if (StatusText.Text == CleaningMessage) ShowStatus(null);
        if (_cleanStatusText != null && _selected switch { AudioPart a => a.Clip, MusicClip m => (IAudioClip)m, _ => null } is { } sel)
            _cleanStatusText.Text = CleanStatus(sel);
    }

    private static string CleanStatus(IAudioClip c) =>
        !c.HasCleanup() ? "Off · the preview plays the original sound."
        : AudioProxy.IsPending(c) ? "Preparing the cleaned preview…"
        : AudioProxy.Get(c) != null ? "Preview plays the cleaned sound. Export uses the same filters."
        : "Applied on export. (The preview couldn't be prepared.)";

    /// <summary>
    /// For a video clip with clean-up on, mutes the video's own sound and plays the cleaned proxy at the
    /// same source position. Returns true when the proxy is handling the sound.
    /// </summary>
    private bool UpdateCleanAudio(VideoClip? clip, double src)
    {
        var proxy = clip != null && clip.PlaysAudio && !clip.IsImage && _playing && !_holdOriginal ? AudioProxy.Get(clip) : null;
        if (proxy == null)
        {
            if (_cleanRunning) { CleanPlayer.Pause(); _cleanRunning = false; }
            return false;
        }
        if (!string.Equals(_cleanPath, proxy, StringComparison.OrdinalIgnoreCase))
        {
            _cleanPath = proxy;
            _cleanOpen = false;
            _cleanRunning = false;
            try
            {
                CleanPlayer.Source = new Uri(proxy);
                CleanPlayer.Pause();
            }
            catch
            {
                return false;
            }
        }
        if (!_cleanOpen) return false; // original sound until the proxy has opened
        CleanPlayer.Volume = Math.Min(1, clip!.Volume);
        if (Math.Abs(CleanPlayer.SpeedRatio - clip.Speed) > 0.001) CleanPlayer.SpeedRatio = clip.Speed;
        // follow the picture that is actually showing
        var target = _playerRunning ? Player.Position.TotalSeconds : src;
        if (!_cleanRunning)
        {
            CleanPlayer.Position = TimeSpan.FromSeconds(target);
            CleanPlayer.Play();
            _cleanRunning = true;
        }
        else if (Math.Abs(CleanPlayer.Position.TotalSeconds - target) > 0.08 && Environment.TickCount64 - _cleanSyncAt > 500)
        {
            // Re-seek at most twice a second so MediaElement's coarse position reports can't cause a seek storm.
            CleanPlayer.Position = TimeSpan.FromSeconds(target);
            _cleanSyncAt = Environment.TickCount64;
        }
        return true;
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        _playerOpen = true;
        _playerRunning = false;
        UpdatePreview(seek: true);
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _playerOpen = false;
        ShowStatus("This clip can't be previewed here (it still exports). " + e.ErrorException?.Message);
    }

    private void Music_MediaOpened(object sender, RoutedEventArgs e)
    {
        _musicOpen = true;
        _musicRunning = false;
        UpdatePreview(seek: true);
    }

    /// <summary>Points the preview at the playhead: picks the clip, source position, music and titles.</summary>
    private void UpdatePreview(bool seek)
    {
        var t = _playhead;
        Timeline.Playhead = t;
        TimecodeText.Text = Timecode(t);

        var clip = _project.VideoAt(t);
        if (clip == null)
        {
            if (_playerRunning) { Player.Pause(); _playerRunning = false; }
            Player.Visibility = Visibility.Hidden;
            StillImage.Visibility = Visibility.Collapsed;
            UpdateCleanAudio(null, 0);
        }
        else if (clip.IsImage)
        {
            if (_playerRunning) { Player.Pause(); _playerRunning = false; }
            Player.Visibility = Visibility.Hidden;
            StillImage.Source = LoadStill(clip.Path);
            StillImage.Visibility = Visibility.Visible;
            UpdateCleanAudio(null, 0);
        }
        else
        {
            StillImage.Visibility = Visibility.Collapsed;
            Player.Visibility = Visibility.Visible;
            var src = clip.SourceTimeAt(t);
            var clipChanged = clip.Id != _playerClip;
            _playerClip = clip.Id;
            if (!string.Equals(_playerPath, clip.Path, StringComparison.OrdinalIgnoreCase))
            {
                _playerPath = clip.Path;
                _playerOpen = false;
                _playerRunning = false;
                try
                {
                    Player.Source = new Uri(clip.Path);
                    Player.Pause(); // opens the file and shows a frame
                }
                catch (Exception ex)
                {
                    ShowStatus(ex.Message);
                }
            }
            if (_playerOpen)
            {
                var cleaned = UpdateCleanAudio(clip, src);
                Player.Volume = clip.PlaysAudio && !cleaned ? Math.Min(1, clip.Volume) : 0;
                if (Math.Abs(Player.SpeedRatio - clip.Speed) > 0.001) Player.SpeedRatio = clip.Speed;
                var drift = Math.Abs(Player.Position.TotalSeconds - src);
                if (_playing)
                {
                    if (!_playerRunning)
                    {
                        Player.Position = TimeSpan.FromSeconds(src);
                        Player.Play();
                        _playerRunning = true;
                    }
                    else if (clipChanged || drift > 0.35)
                    {
                        Player.Position = TimeSpan.FromSeconds(src);
                    }
                }
                else
                {
                    if (_playerRunning) { Player.Pause(); _playerRunning = false; }
                    if (seek || clipChanged || drift > 0.5 / Math.Max(1, _project.Fps))
                        Player.Position = TimeSpan.FromSeconds(src);
                }
            }
        }

        UpdateMusic(t);
        UpdateTitles(t);

        if (_playing || clip == null || !clip.HasColor)
            HideExactFrame();
        else
        {
            var key = $"{clip.Id}|{clip.SourceTimeAt(t):0.000}|{ExportBuilder.ColorFilter(clip)}|{_project.Width}";
            if (key != _exactKey)
            {
                HideExactFrame();
                _exactKey = key;
                _exactTimer.Stop();
                _exactTimer.Start();
            }
        }
    }

    private void UpdateMusic(double t)
    {
        var music = _playing ? _project.Music.LastOrDefault(m => t >= m.Start && t < m.End && m.PlaysAudio) : null;
        if (music == null)
        {
            if (_musicRunning) { MusicPlayer.Pause(); _musicRunning = false; }
            return;
        }
        var src = music.In + (t - music.Start);
        var changed = music.Id != _musicClip;
        _musicClip = music.Id;
        // The cleaned proxy has the same timing as the source, so the same position works for both.
        var path = (_holdOriginal ? null : AudioProxy.Get(music)) ?? music.Path;
        if (!string.Equals(_musicPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _musicPath = path;
            _musicOpen = false;
            _musicRunning = false;
            try
            {
                MusicPlayer.Source = new Uri(path);
                MusicPlayer.Pause();
            }
            catch
            {
                return;
            }
        }
        if (!_musicOpen) return;
        // approximate fades for preview
        var fade = 1.0;
        if (music.FadeIn > 0.01) fade = Math.Min(fade, (t - music.Start) / music.FadeIn);
        if (music.FadeOut > 0.01) fade = Math.Min(fade, (music.End - t) / music.FadeOut);
        MusicPlayer.Volume = Math.Clamp(music.Volume * Math.Clamp(fade, 0, 1), 0, 1);
        if (!_musicRunning)
        {
            MusicPlayer.Position = TimeSpan.FromSeconds(src);
            MusicPlayer.Play();
            _musicRunning = true;
        }
        else if (changed || Math.Abs(MusicPlayer.Position.TotalSeconds - src) > 0.4)
        {
            MusicPlayer.Position = TimeSpan.FromSeconds(src);
        }
    }

    private BitmapImage? LoadStill(string path)
    {
        if (_stills.TryGetValue(path, out var img)) return img;
        try
        {
            img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.UriSource = new Uri(path);
            img.EndInit();
            img.Freeze();
            _stills[path] = img;
            return img;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Draws the titles active at <paramref name="t"/> over the preview, in project pixels.</summary>
    private void UpdateTitles(double t)
    {
        var active = _project.Titles.Where(x => t >= x.Start && t < x.End && !string.IsNullOrWhiteSpace(x.Text)).ToList();
        var signature = string.Join("|", active.Select(x => $"{x.Id}{x.Text}{x.FontSize}{x.Position}{x.Color}{x.Box}")) + $"@{_project.Width}x{_project.Height}";
        if (signature == _titleSignature) return;
        _titleSignature = signature;
        TitleLayer.Children.Clear();
        var W = _project.Width;
        var H = _project.Height;
        var scale = H / 1080.0;
        foreach (var title in active)
        {
            var size = Math.Max(8, title.FontSize * scale);
            var fg = title.Color switch
            {
                TitleColor.Black => Color.FromRgb(0x11, 0x11, 0x11),
                TitleColor.Accent => Color.FromRgb(0xFF, 0xB0, 0x20),
                _ => Colors.White,
            };
            var text = new TextBlock
            {
                Text = title.Text.Replace("\r\n", "\n"),
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.Bold,
                FontSize = size,
                Foreground = new SolidColorBrush(fg),
                TextAlignment = title.Position == TitlePosition.LowerThird ? TextAlignment.Left : TextAlignment.Center,
                LineHeight = size * 1.2,
            };
            var holder = new Border
            {
                Child = text,
                Padding = title.Box ? new Thickness(size * 0.35) : new Thickness(0),
                Background = title.Box
                    ? new SolidColorBrush(title.Color == TitleColor.Black ? Color.FromArgb(204, 255, 255, 255) : Color.FromArgb(140, 0, 0, 0))
                    : Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            switch (title.Position)
            {
                case TitlePosition.Top:
                    holder.VerticalAlignment = VerticalAlignment.Top;
                    holder.Margin = new Thickness(0, H * 0.08 - holder.Padding.Top, 0, 0);
                    break;
                case TitlePosition.Center:
                    holder.VerticalAlignment = VerticalAlignment.Center;
                    break;
                case TitlePosition.LowerThird:
                    holder.VerticalAlignment = VerticalAlignment.Top;
                    holder.HorizontalAlignment = HorizontalAlignment.Left;
                    holder.Margin = new Thickness(W * 0.06 - holder.Padding.Left, H * 0.72 - holder.Padding.Top, 0, 0);
                    break;
                default:
                    holder.VerticalAlignment = VerticalAlignment.Bottom;
                    holder.Margin = new Thickness(0, 0, 0, H * 0.08 - holder.Padding.Bottom);
                    break;
            }
            TitleLayer.Children.Add(holder);
        }
    }

    private void HideExactFrame()
    {
        _exactCts?.Cancel();
        _exactKey = "";
        _exactTimer.Stop();
        ExactFrame.Visibility = Visibility.Collapsed;
    }

    /// <summary>Paused on a colour-graded clip: show the exact ffmpeg-rendered frame.</summary>
    private async Task RenderExactFrameAsync()
    {
        if (_playing) return;
        var clip = _project.VideoAt(_playhead);
        if (clip == null || !clip.HasColor) return;
        var key = _exactKey;
        _exactCts?.Cancel();
        var cts = _exactCts = new CancellationTokenSource();
        try
        {
            var width = Math.Min(1280, Math.Max(320, _project.Width));
            var frame = await FFmpeg.FrameAsync(clip.Path, clip.IsImage ? 0 : clip.SourceTimeAt(_playhead), width, ExportBuilder.ColorFilter(clip), cts.Token);
            if (frame == null || cts.IsCancellationRequested || key != _exactKey || _playing) return;
            ExactFrame.Source = frame;
            ExactFrame.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ================================================================ properties panel

    private TextBox? _titleBox;
    private bool _syncing;

    private void BuildProperties()
    {
        _syncing = true;
        PropertiesHost.Children.Clear();
        _titleBox = null;
        _cleanStatusText = null;
        switch (_selected)
        {
            case VideoClip v: BuildVideoProperties(v); break;
            case AudioPart a: BuildAudioProperties(a.Clip); break;
            case MusicClip m: BuildMusicProperties(m); break;
            case TitleClip t: BuildTitleProperties(t); break;
            default: BuildProjectProperties(); break;
        }
        _syncing = false;
    }

    private TextBlock Caption(string text, double top = 14)
    {
        var tb = new TextBlock { Text = text, Margin = new Thickness(0, top, 0, 6) };
        tb.SetResourceReference(StyleProperty, "Caption");
        return tb;
    }

    private void AddHeading(string text, string? detail = null)
    {
        var title = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = text };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        PropertiesHost.Children.Add(title);
        if (detail != null)
        {
            var d = Caption(detail, 2);
            d.Margin = new Thickness(0, 2, 0, 0);
            PropertiesHost.Children.Add(d);
        }
    }

    private void AddSection(string text)
    {
        var line = new Border { Margin = new Thickness(0, 16, 0, 0) };
        line.SetResourceReference(StyleProperty, "Divider");
        PropertiesHost.Children.Add(line);
        var t = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        PropertiesHost.Children.Add(t);
    }

    private Slider AddSlider(string label, double min, double max, double value, Func<double, string> format, Action<double> apply, string key,
        double step = 0.01, bool enabled = true)
    {
        var header = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        var name = new TextBlock { Text = label, FontSize = 12.5 };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextSoft");
        var valueText = new TextBlock { Text = format(value), FontSize = 12 };
        valueText.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        valueText.SetResourceReference(TextBlock.FontFamilyProperty, "Mono");
        DockPanel.SetDock(valueText, Dock.Right);
        header.Children.Add(name);
        header.Children.Add(valueText);
        PropertiesHost.Children.Add(header);
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), SmallChange = step, LargeChange = step * 10,
            TickFrequency = step, IsSnapToTickEnabled = true, IsEnabled = enabled, Margin = new Thickness(0, 2, 0, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(slider, label);
        slider.ValueChanged += (_, e) =>
        {
            valueText.Text = format(e.NewValue);
            if (_syncing) return;
            Mutate(() => apply(e.NewValue), key);
        };
        slider.PreviewMouseLeftButtonUp += (_, _) => _history.Break();
        PropertiesHost.Children.Add(slider);
        return slider;
    }

    private void AddToggle(string label, bool value, Action<bool> apply, bool enabled = true)
    {
        var box = new CheckBox { Content = label, IsChecked = value, IsEnabled = enabled, Margin = new Thickness(0, 14, 0, 0) };
        box.SetResourceReference(StyleProperty, "SwitchToggle");
        box.Checked += (_, _) => { if (!_syncing) Mutate(() => apply(true)); };
        box.Unchecked += (_, _) => { if (!_syncing) Mutate(() => apply(false)); };
        PropertiesHost.Children.Add(box);
    }

    private void AddCombo(string label, IReadOnlyList<string> items, int index, Action<int> apply)
    {
        PropertiesHost.Children.Add(Caption(label));
        var combo = new ComboBox { ItemsSource = items, SelectedIndex = index };
        System.Windows.Automation.AutomationProperties.SetName(combo, label);
        combo.SelectionChanged += (_, _) =>
        {
            if (_syncing || combo.SelectedIndex < 0) return;
            Mutate(() => apply(combo.SelectedIndex));
        };
        PropertiesHost.Children.Add(combo);
    }

    private void AddLink(string text, Action onClick)
    {
        var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        b.SetResourceReference(StyleProperty, "LinkButton");
        b.Click += (_, _) => onClick();
        PropertiesHost.Children.Add(b);
    }

    private static string Seconds(double v) => $"{v:0.0} s";
    private static string Percent(double v) => $"{v * 100:0}%";
    private static string Signed(double v) => v > 0.0005 ? $"+{v:0.00}" : $"{v:0.00}";

    private static readonly double[] Speeds = { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4 };

    private void BuildVideoProperties(VideoClip v)
    {
        PropertiesTitle.Text = v.IsImage ? "Image clip" : "Video clip";
        AddHeading(v.Name, v.IsImage ? $"{Seconds(v.Duration)} on the timeline" : $"{FormatShort(v.Duration)} · from {FormatShort(v.In)} to {FormatShort(v.Out)}");

        if (v.IsImage)
        {
            AddSlider("Duration", 0.5, 30, v.Out - v.In, Seconds, x => { v.In = 0; v.Out = x; }, $"{v.Id}:dur", 0.1);
        }
        else
        {
            var speedIndex = Array.FindIndex(Speeds, s => Math.Abs(s - v.Speed) < 0.001);
            AddCombo("Speed", Speeds.Select(s => $"{s:0.##}×").ToList(), speedIndex < 0 ? 3 : speedIndex, i => v.Speed = Speeds[i]);
        }

        AddSection("Audio");
        if (!v.HasAudio || v.IsImage)
        {
            PropertiesHost.Children.Add(Caption("This clip has no sound.", 6));
        }
        else
        {
            PropertiesHost.Children.Add(Caption(AudioSummary(v), 6));
            AddLink("Edit audio", () => Select(new AudioPart(v)));
        }

        AddSection("Fade");
        var maxFade = Math.Max(0.1, Math.Min(5, v.Duration / 2));
        AddSlider("Fade in", 0, maxFade, v.FadeIn, Seconds, x => v.FadeIn = x, $"{v.Id}:fi", 0.1);
        AddSlider("Fade out", 0, maxFade, v.FadeOut, Seconds, x => v.FadeOut = x, $"{v.Id}:fo", 0.1);

        AddSection("Colour");
        AddSlider("Brightness", -0.5, 0.5, v.Brightness, Signed, x => v.Brightness = x, $"{v.Id}:br", 0.01);
        AddSlider("Contrast", 0.5, 1.5, v.Contrast, Percent, x => v.Contrast = x, $"{v.Id}:co", 0.01);
        AddSlider("Saturation", 0, 2, v.Saturation, Percent, x => v.Saturation = x, $"{v.Id}:sa", 0.01);
        if (v.HasColor)
            AddLink("Reset colour", () => Mutate(() => { v.Brightness = 0; v.Contrast = 1; v.Saturation = 1; }, rebuildProperties: true));
    }

    private static string AudioSummary(IAudioClip c)
    {
        if (c.Muted) return "Audio removed.";
        var parts = new List<string> { Percent(c.Volume) + " volume" };
        if (c.Enhance) parts.Add($"enhanced ({c.Style.ToString().ToLowerInvariant()})");
        if (c.NoiseReduction != NoiseLevel.Off) parts.Add($"noise reduction {c.NoiseReduction.ToString().ToLowerInvariant()}");
        if (c.RemoveHum) parts.Add("hum filter");
        if (c.Normalize) parts.Add("normalized");
        return string.Join(" · ", parts);
    }

    /// <summary>The A1 audio of a video clip, edited on its own.</summary>
    private void BuildAudioProperties(VideoClip v)
    {
        PropertiesTitle.Text = "Audio";
        AddHeading(v.Name, $"{FormatShort(v.Duration)} · sound of this video clip");
        AddAudioControls(v, removeLabel: "Remove audio");
        AddSection("Fade (with the video)");
        var maxFade = Math.Max(0.1, Math.Min(5, v.Duration / 2));
        AddSlider("Fade in", 0, maxFade, v.FadeIn, Seconds, x => v.FadeIn = x, $"{v.Id}:fi", 0.1);
        AddSlider("Fade out", 0, maxFade, v.FadeOut, Seconds, x => v.FadeOut = x, $"{v.Id}:fo", 0.1);
        AddLink("Select the video", () => Select(v));
    }

    private void BuildMusicProperties(MusicClip m)
    {
        PropertiesTitle.Text = "Music";
        AddHeading(m.Name, $"{FormatShort(m.Duration)} · starts at {FormatShort(m.Start)}");
        AddAudioControls(m, removeLabel: "Mute");
        AddSection("Fade");
        var maxFade = Math.Max(0.1, Math.Min(10, m.Duration / 2));
        AddSlider("Fade in", 0, maxFade, m.FadeIn, Seconds, x => m.FadeIn = x, $"{m.Id}:fi", 0.1);
        AddSlider("Fade out", 0, maxFade, m.FadeOut, Seconds, x => m.FadeOut = x, $"{m.Id}:fo", 0.1);
    }

    private static readonly string[] NoiseNames = { "Off", "Light", "Medium", "Strong" };

    /// <summary>Volume, remove/mute and the clean-up section, shared by audio parts and music.</summary>
    private void AddAudioControls(IAudioClip c, string removeLabel)
    {
        AddToggle(removeLabel, c.Muted, x => { c.Muted = x; Dispatcher.BeginInvoke(BuildProperties); });
        AddSlider("Volume", 0, 2, c.Volume, Percent, x => c.Volume = x, $"{c.Id}:vol", 0.01, enabled: !c.Muted);

        AddEnhanceCard(c);

        AddSection("Clean up");
        PropertiesHost.Children.Add(Caption("Noise reduction", 8));
        AddSegmented(NoiseNames, (int)c.NoiseReduction, i => c.NoiseReduction = (NoiseLevel)i, !c.Muted,
            "Removes steady background noise such as fans, hiss or traffic. Start with Light.");
        AddToggle("Hum & rumble filter", c.RemoveHum, x => c.RemoveHum = x, !c.Muted);
        LastToolTip("Cuts low rumble and 50/60 Hz electrical hum.");
        AddToggle("Normalize this clip", c.Normalize, x => c.Normalize = x, !c.Muted);
        LastToolTip("A quick single pass on this clip only, aiming for −16 LUFS. It doesn't even out speech within the clip (use Enhance voice) and isn't the final level: the project's loudness target sets the whole export precisely.");
        _cleanStatusText = Caption(CleanStatus(c), 10);
        PropertiesHost.Children.Add(_cleanStatusText);
    }

    private void LastToolTip(string tip)
    {
        if (PropertiesHost.Children.Count > 0 && PropertiesHost.Children[^1] is FrameworkElement fe) fe.ToolTip = tip;
    }

    /// <summary>A segmented control (theme "Segment" radio buttons) that applies one undoable change.</summary>
    private void AddSegmented(IReadOnlyList<string> items, int index, Action<int> apply, bool enabled, string? tip = null)
    {
        var host = new Border { HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = enabled, ToolTip = tip };
        host.SetResourceReference(StyleProperty, "SegmentHost");
        // Equal columns across the panel width, so labels like "Strong" never get cut off.
        var panel = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1 };
        var group = "seg" + Guid.NewGuid().ToString("N");
        for (var i = 0; i < items.Count; i++)
        {
            var value = i;
            // The shared Segment style pads 12 px each side; pull the label out a little so four fit the panel.
            var label = new TextBlock { Text = items[i], Margin = new Thickness(-8, 0, -8, 0), TextAlignment = TextAlignment.Center };
            var rb = new RadioButton { Content = label, GroupName = group, IsChecked = i == index, MinWidth = 0, FontSize = 12.5 };
            System.Windows.Automation.AutomationProperties.SetName(rb, items[i]);
            rb.SetResourceReference(StyleProperty, "Segment");
            rb.Checked += (_, _) => { if (!_syncing) Mutate(() => apply(value)); };
            panel.Children.Add(rb);
        }
        host.Child = panel;
        PropertiesHost.Children.Add(host);
    }

    private static readonly string[] PositionNames = { "Top", "Centre", "Bottom", "Lower third" };
    private static readonly TitlePosition[] Positions = { TitlePosition.Top, TitlePosition.Center, TitlePosition.Bottom, TitlePosition.LowerThird };
    private static readonly string[] ColorNames = { "White", "Black", "Amber" };

    private void BuildTitleProperties(TitleClip t)
    {
        PropertiesTitle.Text = "Title";
        PropertiesHost.Children.Add(Caption("Text", 2));
        var box = new TextBox
        {
            Text = t.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 64,
            MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        System.Windows.Automation.AutomationProperties.SetName(box, "Title text");
        box.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            Mutate(() => t.Text = box.Text, $"{t.Id}:text");
        };
        box.LostKeyboardFocus += (_, _) => _history.Break();
        PropertiesHost.Children.Add(box);
        _titleBox = box;

        AddCombo("Position", PositionNames, Array.IndexOf(Positions, t.Position), i => t.Position = Positions[i]);
        AddCombo("Colour", ColorNames, (int)t.Color, i => t.Color = (TitleColor)i);
        AddSlider("Size", 24, 180, t.FontSize, x => $"{x:0}", x => t.FontSize = x, $"{t.Id}:size", 1);
        AddSlider("Duration", 0.5, 30, t.Duration, Seconds, x => t.Duration = x, $"{t.Id}:dur", 0.1);
        AddToggle("Background box", t.Box, x => t.Box = x);
    }

    private static readonly (string Label, int W, int H)[] Resolutions =
    {
        ("1920 × 1080 · 16:9", 1920, 1080), ("1280 × 720 · 16:9", 1280, 720), ("3840 × 2160 · 4K", 3840, 2160),
        ("1080 × 1920 · vertical", 1080, 1920), ("1080 × 1080 · square", 1080, 1080),
    };
    private static readonly double[] Rates = { 24, 25, 30, 50, 60 };

    private void BuildProjectProperties()
    {
        PropertiesTitle.Text = "Project";
        var clips = _project.Video.Count;
        AddHeading(_projectPath != null ? Path.GetFileNameWithoutExtension(_projectPath) : _project.Name,
            clips == 0 ? "Empty timeline" : $"{clips} clip{(clips == 1 ? "" : "s")} · {FormatShort(_project.Duration)}");

        var labels = Resolutions.Select(r => r.Label).ToList();
        var index = Array.FindIndex(Resolutions, r => r.W == _project.Width && r.H == _project.Height);
        if (index < 0)
        {
            labels.Add($"{_project.Width} × {_project.Height} · from first clip");
            index = labels.Count - 1;
        }
        AddCombo("Frame size", labels, index, i =>
        {
            if (i < Resolutions.Length) { _project.Width = Resolutions[i].W; _project.Height = Resolutions[i].H; }
        });

        var rateLabels = Rates.Select(r => $"{r:0} fps").ToList();
        var rateIndex = Array.FindIndex(Rates, r => Math.Abs(r - _project.Fps) < 0.05);
        if (rateIndex < 0)
        {
            rateLabels.Add($"{_project.Fps:0.##} fps");
            rateIndex = rateLabels.Count - 1;
        }
        AddCombo("Frame rate", rateLabels, rateIndex, i => { if (i < Rates.Length) _project.Fps = Rates[i]; });

        AddSoundSection();

        var hint = Caption("Select a clip on the timeline to edit it.", 18);
        PropertiesHost.Children.Add(hint);
    }

    // ================================================================ export

    private static readonly (string Label, int W, int H)[] Presets =
    {
        ("1080p · 1920 × 1080", 1920, 1080), ("720p · 1280 × 720", 1280, 720), ("4K · 3840 × 2160", 3840, 2160),
        ("Vertical · 1080 × 1920", 1080, 1920), ("Square · 1080 × 1080", 1080, 1080),
    };
    private sealed record ExportChoice(string Label, int W, int H, AudioFormat? Audio);
    private List<ExportChoice> _presetList = new();
    private List<string?> _encoderList = new();
    private bool _videoEncoderMissing;

    private ExportChoice CurrentChoice => _presetList[Math.Clamp(PresetCombo.SelectedIndex, 0, Math.Max(0, _presetList.Count - 1))];

    private string ExportExtension(ExportChoice c) => c.Audio is { } a ? ExportBuilder.Extension(a) : ".mp4";

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!_project.CanExport) return;
        if (!FFmpeg.IsAvailable) { SetupOverlay.Visibility = Visibility.Visible; return; }
        Pause();
        _presetList = new List<ExportChoice>();
        if (_project.Video.Count > 0)
        {
            _presetList.Add(new ExportChoice($"Project · {_project.Width} × {_project.Height}", _project.Width, _project.Height, null));
            _presetList.AddRange(Presets.Where(p => p.W != _project.Width || p.H != _project.Height).Select(p => new ExportChoice(p.Label, p.W, p.H, null)));
        }
        var encoders = await FFmpeg.EncodersAsync();
        _presetList.Add(new ExportChoice("Audio only · M4A (AAC)", 0, 0, AudioFormat.Aac));
        if (encoders.Contains("libmp3lame")) _presetList.Add(new ExportChoice("Audio only · MP3", 0, 0, AudioFormat.Mp3));
        _presetList.Add(new ExportChoice("Audio only · WAV (lossless)", 0, 0, AudioFormat.Wav));
        PresetCombo.ItemsSource = _presetList.Select(p => p.Label).ToList();
        ExportPathBox.Text = UniquePath(Path.Combine(DefaultFolder(), SafeName(_projectPath != null ? Path.GetFileNameWithoutExtension(_projectPath) : _project.Name) + ExportExtension(_presetList[0])));
        PresetCombo.SelectedIndex = 0;
        SetExportState(running: false, done: false);
        ExportErrorText.Visibility = Visibility.Collapsed;
        _syncing = true;
        (_project.LoudnessTarget switch { LoudnessTarget.YouTube => LoudYouTube, LoudnessTarget.Podcast => LoudPodcast, _ => LoudOff }).IsChecked = true;
        _syncing = false;
        ExportOverlay.Visibility = Visibility.Visible;
        ApplyExportChoice();
        ExportStartButton.Focus();

        EncoderCombo.ItemsSource = new[] { "Checking encoders…" };
        EncoderCombo.SelectedIndex = 0;
        EncoderCombo.IsEnabled = false;
        var working = await FFmpeg.WorkingEncodersAsync();
        _encoderList = new List<string?> { null };
        _encoderList.AddRange(working);
        var auto = working.FirstOrDefault(w => w is "libx264" or "h264_nvenc" or "h264_qsv" or "h264_amf" or "h264_mf");
        EncoderCombo.ItemsSource = _encoderList.Select(x => x == null ? $"Automatic{(auto != null ? " · " + FFmpeg.EncoderLabel(auto) : "")}" : FFmpeg.EncoderLabel(x)).ToList();
        EncoderCombo.SelectedIndex = 0;
        EncoderCombo.IsEnabled = working.Count > 0;
        _videoEncoderMissing = working.Count == 0;
        ApplyExportChoice();
    }

    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_presetList.Count == 0 || PresetCombo.SelectedIndex < 0) return;
        ApplyExportChoice();
    }

    /// <summary>Video vs audio-only: encoder row, title, file extension and the missing-encoder warning.</summary>
    private void ApplyExportChoice()
    {
        var choice = CurrentChoice;
        var audio = choice.Audio != null;
        EncoderPanel.Visibility = audio ? Visibility.Collapsed : Visibility.Visible;
        ExportTitleText.Text = audio ? "Export audio" : "Export video";
        var path = ExportPathBox.Text.Trim();
        if (path.Length > 0)
        {
            var ext = ExportExtension(choice);
            var known = new[] { ".mp4", ".m4a", ".mp3", ".wav" };
            if (known.Contains(Path.GetExtension(path).ToLowerInvariant()) && !path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                ExportPathBox.Text = Path.ChangeExtension(path, ext);
        }
        if (!audio && _videoEncoderMissing)
        {
            ExportErrorText.Text = "No working video encoder was found in this ffmpeg build. Audio-only export still works.";
            ExportErrorText.Visibility = Visibility.Visible;
            ExportStartButton.IsEnabled = false;
        }
        else if (_exportCts == null)
        {
            if (ExportErrorText.Text.StartsWith("No working video encoder")) ExportErrorText.Visibility = Visibility.Collapsed;
            ExportStartButton.IsEnabled = true;
        }
    }

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "Untitled" : name;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private void SetExportState(bool running, bool done)
    {
        ExportForm.IsEnabled = !running && !done;
        ExportProgressPanel.Visibility = running || done ? Visibility.Visible : Visibility.Collapsed;
        ExportButtons.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        ExportDoneButtons.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        ExportStartButton.IsEnabled = !running;
        ExportCancelButton.Content = running ? "Stop" : "Cancel";
        ExportCloseButton.IsEnabled = !running;
    }

    private void ExportBrowse_Click(object sender, RoutedEventArgs e)
    {
        var current = ExportPathBox.Text;
        var choice = CurrentChoice;
        var dialog = new SaveFileDialog
        {
            Filter = choice.Audio switch
            {
                AudioFormat.Mp3 => "MP3 audio (*.mp3)|*.mp3",
                AudioFormat.Wav => "WAV audio (*.wav)|*.wav",
                AudioFormat.Aac => "M4A audio (*.m4a)|*.m4a",
                _ => "MP4 video (*.mp4)|*.mp4",
            },
            FileName = Path.GetFileName(current),
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(current)) ? Path.GetDirectoryName(current) : DefaultFolder(),
            Title = choice.Audio != null ? "Export audio" : "Export video",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ExportPathBox.Text = dialog.FileName;
    }

    private async void ExportStart_Click(object sender, RoutedEventArgs e)
    {
        var output = ExportPathBox.Text.Trim();
        if (string.IsNullOrEmpty(output) || !Path.IsPathRooted(output))
        {
            ExportErrorText.Text = "Choose where to save the video.";
            ExportErrorText.Visibility = Visibility.Visible;
            return;
        }
        var choice = CurrentChoice;
        var ext = ExportExtension(choice);
        if (!output.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) output += ext;
        var missing = _project.Video.Select(v => v.Path).Concat(_project.Music.Select(m => m.Path)).Distinct().Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            ExportErrorText.Text = "Missing media: " + string.Join(", ", missing.Select(Path.GetFileName).Take(3));
            ExportErrorText.Visibility = Visibility.Visible;
            return;
        }

        string? encoder = "";
        if (choice.Audio == null)
        {
            encoder = EncoderCombo.SelectedIndex > 0 ? _encoderList[EncoderCombo.SelectedIndex] : await FFmpeg.ResolveEncoderAsync(hevc: false);
            if (encoder == null)
            {
                ExportErrorText.Text = "No working video encoder was found.";
                ExportErrorText.Visibility = Visibility.Visible;
                return;
            }
        }
        var quality = QualityHigh.IsChecked == true ? ExportQuality.High : QualitySmall.IsChecked == true ? ExportQuality.Small : ExportQuality.Standard;
        var options = new ExportOptions(choice.W, choice.H, _project.Fps, quality, encoder, output, choice.Audio);
        var snapshot = EditorProject.FromJson(_project.ToJson()); // export a frozen copy; editing can continue

        ExportErrorText.Visibility = Visibility.Collapsed;
        ExportProgressBar.Value = 0;
        ExportStatusText.Text = "Starting…";
        SetExportState(running: true, done: false);
        _exportCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<ExportProgress>(p =>
            {
                ExportProgressBar.Value = p.Fraction;
                ExportStatusText.Text = p.Stage ?? ($"{p.Fraction * 100:0}%" + (p.Remaining is { } r ? $" · about {FormatRemaining(r)} left" : ""));
            });
            var result = await Task.Run(() => ExportBuilder.ExportAsync(snapshot, options, progress, _exportCts.Token));
            _exportedFile = output;
            ExportProgressBar.Value = 1;
            ExportStatusText.Text = "Saved to " + output + (result.Loudness is { } l ? $"\nLoudness {l}" : "");
            SetExportState(running: false, done: true);
        }
        catch (OperationCanceledException)
        {
            SetExportState(running: false, done: false);
            ExportProgressPanel.Visibility = Visibility.Collapsed;
            ExportErrorText.Text = "Export stopped.";
            ExportErrorText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            SetExportState(running: false, done: false);
            ExportProgressPanel.Visibility = Visibility.Collapsed;
            ExportErrorText.Text = ex.Message;
            ExportErrorText.ToolTip = ex.InnerException?.Message;
            ExportErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            _exportCts?.Dispose();
            _exportCts = null;
        }
    }

    private static string FormatRemaining(TimeSpan r) =>
        r.TotalSeconds < 60 ? $"{Math.Max(1, (int)r.TotalSeconds)} s" : r.TotalMinutes < 60 ? $"{(int)Math.Ceiling(r.TotalMinutes)} min" : $"{r.TotalHours:0.0} h";

    private void ExportCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_exportCts != null) _exportCts.Cancel();
        else ExportOverlay.Visibility = Visibility.Collapsed;
    }

    private void ExportClose_Click(object sender, RoutedEventArgs e)
    {
        if (_exportCts == null) ExportOverlay.Visibility = Visibility.Collapsed;
    }

    private void ExportOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_exportedFile == null) return;
        try { Process.Start(new ProcessStartInfo(_exportedFile) { UseShellExecute = true }); } catch { }
        ExportOverlay.Visibility = Visibility.Collapsed;
    }

    private void ExportShowFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_exportedFile == null) return;
        try { Process.Start("explorer.exe", $"/select,\"{_exportedFile}\""); } catch { }
        ExportOverlay.Visibility = Visibility.Collapsed;
    }

    // ================================================================ ffmpeg setup

    private void LocateFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "ffmpeg.exe|ffmpeg.exe", Title = "Locate ffmpeg.exe" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (FFmpeg.SetCustomPath(dialog.FileName))
        {
            SetupOverlay.Visibility = Visibility.Collapsed;
            SetupErrorText.Visibility = Visibility.Collapsed;
            _ = FFmpeg.WorkingEncodersAsync();
        }
        else
        {
            SetupErrorText.Text = "ffprobe.exe wasn't found next to that ffmpeg.exe.";
            SetupErrorText.Visibility = Visibility.Visible;
        }
    }

    private void DownloadFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://ffmpeg.org/download.html") { UseShellExecute = true }); } catch { }
    }

    // ================================================================ voice: enhance, pauses, loudness

    private static readonly string[] StyleNames = { "Natural", "Podcast", "Broadcast" };
    private static readonly string[] LoudnessNames = { "Off", "YouTube", "Podcast" };
    private static readonly string[] DuckNames = { "Light", "Medium", "Strong" };
    private bool _holdOriginal;
    private Button? _holdButton;
    private string? _loudnessResult;
    private bool _measuring;

    /// <summary>"Enhance voice": switch, style, hold-to-compare, remove pauses, apply to all.</summary>
    private void AddEnhanceCard(IAudioClip c)
    {
        AddSection("Voice");
        AddToggle("Enhance voice", c.Enhance, x =>
        {
            c.Enhance = x;
            if (x && c.NoiseReduction == NoiseLevel.Off) c.NoiseReduction = NoiseLevel.Light; // part of the chain
            Dispatcher.BeginInvoke(BuildProperties);
        }, !c.Muted);
        LastToolTip("One click for clear speech: repairs clicks and clipping, removes rumble and noise, adds clarity, tames harsh “s” sounds and evens out the level.");
        PropertiesHost.Children.Add(Caption("Style", 8));
        AddSegmented(StyleNames, (int)c.Style, i => c.Style = (VoiceStyle)i, !c.Muted && c.Enhance,
            "Natural: a light touch. Podcast: fuller and more even. Broadcast: strongest, radio-like.");

        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var hold = new Button { Content = "Hold to hear original", Margin = new Thickness(0, 0, 8, 6),
            ToolTip = "Press and hold (or hold Space) to hear the sound without clean-up. Release to hear it cleaned." };
        hold.SetResourceReference(StyleProperty, "BaseButton");
        hold.IsEnabled = c.HasCleanup() && !c.Muted;
        hold.PreviewMouseLeftButtonDown += (_, e) => { hold.CaptureMouse(); SetHoldOriginal(true); e.Handled = true; };
        hold.PreviewMouseLeftButtonUp += (_, e) => { SetHoldOriginal(false); hold.ReleaseMouseCapture(); e.Handled = true; };
        hold.LostMouseCapture += (_, _) => SetHoldOriginal(false);
        hold.PreviewKeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) { if (!e.IsRepeat) SetHoldOriginal(true); e.Handled = true; } };
        hold.PreviewKeyUp += (_, e) => { if (e.Key is Key.Space or Key.Enter) { SetHoldOriginal(false); e.Handled = true; } };
        hold.LostKeyboardFocus += (_, _) => SetHoldOriginal(false);
        System.Windows.Automation.AutomationProperties.SetName(hold, "Hold to hear original");
        _holdButton = hold;
        row.Children.Add(hold);

        var pauses = new Button { Content = "Remove pauses", Margin = new Thickness(0, 0, 8, 6),
            ToolTip = $"Cuts silences longer than {PauseDetector.MinPause:0.0} s from this clip, keeping {PauseDetector.Padding:0.0} s either side. Undo restores them." };
        pauses.SetResourceReference(StyleProperty, "GhostButton");
        pauses.IsEnabled = !c.Muted;
        pauses.Click += (_, _) => RemovePauses(c);
        row.Children.Add(pauses);
        PropertiesHost.Children.Add(row);

        var kind = c is MusicClip ? "music clips" : "clips";
        AddLink($"Apply to all {kind}", () => ApplyVoiceToAll(c));
        LastToolTip("Copies Enhance voice, style and noise reduction to every clip of this kind.");
    }

    private void SetHoldOriginal(bool on)
    {
        if (_holdOriginal == on) return;
        _holdOriginal = on;
        if (_holdButton != null) _holdButton.Content = on ? "Original sound…" : "Hold to hear original";
        if (on && !_playing) Play();
        UpdatePreview(seek: false);
    }

    private void ApplyVoiceToAll(IAudioClip source)
    {
        var targets = source is MusicClip
            ? _project.Music.Cast<IAudioClip>().ToList()
            : _project.Video.Where(v => v.HasAudio && !v.IsImage).Cast<IAudioClip>().ToList();
        Mutate(() =>
        {
            foreach (var t in targets)
            {
                t.Enhance = source.Enhance;
                t.Style = source.Style;
                t.NoiseReduction = source.NoiseReduction;
            }
        }, rebuildProperties: true);
        RequestProxies();
        ShowStatus($"Voice settings applied to {targets.Count} {(source is MusicClip ? "music clip" : "clip")}{(targets.Count == 1 ? "" : "s")}.");
    }

    private async void RemovePauses(IAudioClip c)
    {
        var (path, from, to) = c switch
        {
            VideoClip v => (v.Path, v.In, v.Out),
            MusicClip m => (m.Path, m.In, m.Out),
            _ => ("", 0.0, 0.0),
        };
        if (path.Length == 0) return;
        ShowStatus("Finding pauses…");
        List<(double Start, double End)> cuts;
        try
        {
            cuts = await PauseDetector.FindAsync(path, from, to);
        }
        catch (Exception ex)
        {
            ShowStatus("Couldn't analyse the audio: " + ex.Message);
            return;
        }
        if (cuts.Count == 0)
        {
            ShowStatus($"No pauses longer than {PauseDetector.MinPause:0.0} s in this clip.");
            return;
        }
        (int Count, double Seconds) removed = default;
        Mutate(() => removed = _project.RemoveRanges(c, cuts), rebuildProperties: true);
        ShowStatus(removed.Count == 0 ? "Nothing to remove."
            : $"Removed {removed.Count} pause{(removed.Count == 1 ? "" : "s")} ({removed.Seconds:0.0} s). Undo brings them back.");
    }

    /// <summary>Project panel: loudness target, music ducking and the loudness meter.</summary>
    private void AddSoundSection()
    {
        AddSection("Sound");
        PropertiesHost.Children.Add(Caption("Loudness target", 8));
        AddSegmented(LoudnessNames, (int)_project.LoudnessTarget, i => _project.LoudnessTarget = (LoudnessTarget)i, true,
            "Sets the whole export to the platform's loudness: YouTube −14 LUFS, podcasts −16 LUFS, true peak −1 dBTP. Measured first, then applied.");
        AddToggle("Lower music while speaking", _project.DuckMusic, x => { _project.DuckMusic = x; Dispatcher.BeginInvoke(BuildProperties); });
        LastToolTip("Background music drops automatically whenever the video's sound plays. Applied on export; the preview plays the music at its normal level.");
        PropertiesHost.Children.Add(Caption("Music drop", 8));
        AddSegmented(DuckNames, (int)_project.DuckAmount, i => _project.DuckAmount = (DuckAmount)i, _project.DuckMusic);

        var measure = new Button { Content = _measuring ? "Measuring…" : "Measure loudness", HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 14, 0, 0), IsEnabled = !_measuring && _project.CanExport && FFmpeg.IsAvailable,
            ToolTip = "Measures the whole mix (EBU R128): loudness, true peak and range." };
        measure.SetResourceReference(StyleProperty, "BaseButton");
        measure.Click += async (_, _) => await MeasureLoudnessAsync();
        PropertiesHost.Children.Add(measure);
        if (_loudnessResult != null)
            PropertiesHost.Children.Add(Caption(_loudnessResult, 8));
    }

    private async Task MeasureLoudnessAsync()
    {
        if (_measuring) return;
        _measuring = true;
        BuildProperties();
        var snapshot = EditorProject.FromJson(_project.ToJson());
        try
        {
            var options = new ExportOptions(snapshot.Width, snapshot.Height, snapshot.Fps, ExportQuality.Standard, "", "", AudioFormat.Wav);
            var reading = await Task.Run(() => ExportBuilder.MeasureMixAsync(snapshot, options, null, CancellationToken.None));
            _loudnessResult = reading == null ? "Couldn't measure (is there any sound?)." : $"{reading}\n{Loudness.Verdict(reading, snapshot.LoudnessTarget)}";
        }
        catch (Exception ex)
        {
            _loudnessResult = "Couldn't measure: " + ex.Message;
        }
        finally
        {
            _measuring = false;
        }
        if (_selected == null) BuildProperties();
    }

    private void ExportLoudness_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var target = sender == LoudYouTube ? LoudnessTarget.YouTube : sender == LoudPodcast ? LoudnessTarget.Podcast : LoudnessTarget.Off;
        if (target != _project.LoudnessTarget)
            Mutate(() => _project.LoudnessTarget = target);
    }

    // ---- test hooks (EditorHarness --shots) ----
    internal void VoiceDemoForTest()
    {
        var withAudio = _project.Video.Where(v => v.HasAudio && !v.IsImage).ToList();
        if (withAudio.Count == 0) return;
        Mutate(() =>
        {
            withAudio[0].Enhance = true;
            withAudio[0].Style = VoiceStyle.Podcast;
            withAudio[0].NoiseReduction = NoiseLevel.Medium;
            _project.LoudnessTarget = LoudnessTarget.YouTube;
        });
        Select(new AudioPart(withAudio[0]));
    }
    internal void SetLoudnessResultForTest(string text) { _loudnessResult = text; BuildProperties(); }
}
