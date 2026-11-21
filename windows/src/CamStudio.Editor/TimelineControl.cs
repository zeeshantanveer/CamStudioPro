using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CamStudio.Editor.Engine;
using CamStudio.Editor.Model;

namespace CamStudio.Editor;

/// <summary>
/// The multi-track timeline: ruler, T1 titles, V1 video (magnetic), A1 linked audio, A2 music.
/// Drawn in OnRender for speed (only the visible part); colours are theme brushes bound with
/// SetResourceReference so a theme switch re-renders it. Selectable objects: <see cref="VideoClip"/>,
/// <see cref="AudioPart"/> (a video clip's A1 audio), <see cref="MusicClip"/> and <see cref="TitleClip"/>.
/// Edits mutate the project in place between EditStarting/EditCompleted.
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    public const double RulerH = 26, TitleH = 30, VideoH = 56, AudioH = 34, MusicH = 38, Gap = 6;
    public static double TitleY => RulerH + Gap;
    public static double VideoY => TitleY + TitleH + Gap;
    public static double AudioY => VideoY + VideoH + 2;
    public static double MusicY => AudioY + AudioH + Gap;
    public static double TotalHeight => MusicY + MusicH + Gap;
    /// <summary>Height of the timeline area including the horizontal scrollbar.</summary>
    public static double TotalHeightWithScroll => TotalHeight + 12;

    private const double EdgeGrab = 7, SnapPx = 8, DragThreshold = 3;

    public enum Row { None, Ruler, Titles, Video, Audio, Music }

    // ---------- theme brushes (resource-bound dependency properties) ----------

    private static DependencyProperty BrushProp(string name) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(TimelineControl),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BgBrushProperty = BrushProp(nameof(BgBrush));
    public static readonly DependencyProperty RowBrushProperty = BrushProp(nameof(RowBrush));
    public static readonly DependencyProperty RowSelectedBrushProperty = BrushProp(nameof(RowSelectedBrush));
    public static readonly DependencyProperty LineBrushProperty = BrushProp(nameof(LineBrush));
    public static readonly DependencyProperty TickBrushProperty = BrushProp(nameof(TickBrush));
    public static readonly DependencyProperty LabelBrushProperty = BrushProp(nameof(LabelBrush));
    public static readonly DependencyProperty VideoBrushProperty = BrushProp(nameof(VideoBrush));
    public static readonly DependencyProperty AudioBrushProperty = BrushProp(nameof(AudioBrush));
    public static readonly DependencyProperty MusicBrushProperty = BrushProp(nameof(MusicBrush));
    public static readonly DependencyProperty TitleBrushProperty = BrushProp(nameof(TitleBrush));
    public static readonly DependencyProperty ClipTextBrushProperty = BrushProp(nameof(ClipTextBrush));
    public static readonly DependencyProperty AccentBrushProperty = BrushProp(nameof(AccentBrush));
    public static readonly DependencyProperty SelectBrushProperty = BrushProp(nameof(SelectBrush));
    public static readonly DependencyProperty PlayheadBrushProperty = BrushProp(nameof(PlayheadBrush));
    public static readonly DependencyProperty ScrimBrushProperty = BrushProp(nameof(ScrimBrush));

    public Brush BgBrush { get => (Brush)GetValue(BgBrushProperty); set => SetValue(BgBrushProperty, value); }
    public Brush RowBrush { get => (Brush)GetValue(RowBrushProperty); set => SetValue(RowBrushProperty, value); }
    public Brush RowSelectedBrush { get => (Brush)GetValue(RowSelectedBrushProperty); set => SetValue(RowSelectedBrushProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Brush TickBrush { get => (Brush)GetValue(TickBrushProperty); set => SetValue(TickBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public Brush VideoBrush { get => (Brush)GetValue(VideoBrushProperty); set => SetValue(VideoBrushProperty, value); }
    public Brush AudioBrush { get => (Brush)GetValue(AudioBrushProperty); set => SetValue(AudioBrushProperty, value); }
    public Brush MusicBrush { get => (Brush)GetValue(MusicBrushProperty); set => SetValue(MusicBrushProperty, value); }
    public Brush TitleBrush { get => (Brush)GetValue(TitleBrushProperty); set => SetValue(TitleBrushProperty, value); }
    public Brush ClipTextBrush { get => (Brush)GetValue(ClipTextBrushProperty); set => SetValue(ClipTextBrushProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    /// <summary>Outer selection outline: the theme's text colour (white in dark, near-black in light).</summary>
    public Brush SelectBrush { get => (Brush)GetValue(SelectBrushProperty); set => SetValue(SelectBrushProperty, value); }
    public Brush PlayheadBrush { get => (Brush)GetValue(PlayheadBrushProperty); set => SetValue(PlayheadBrushProperty, value); }
    public Brush ScrimBrush { get => (Brush)GetValue(ScrimBrushProperty); set => SetValue(ScrimBrushProperty, value); }

    public static readonly DependencyProperty PixelsPerSecondProperty = DependencyProperty.Register(
        nameof(PixelsPerSecond), typeof(double), typeof(TimelineControl),
        new FrameworkPropertyMetadata(60.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlayheadProperty = DependencyProperty.Register(
        nameof(Playhead), typeof(double), typeof(TimelineControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double PixelsPerSecond { get => (double)GetValue(PixelsPerSecondProperty); set => SetValue(PixelsPerSecondProperty, value); }
    public double Playhead { get => (double)GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }

    // ---------- state ----------

    private EditorProject? _project;
    public EditorProject? Project
    {
        get => _project;
        set { _project = value; InvalidateMeasure(); InvalidateVisual(); }
    }

    private object? _selected;
    /// <summary>VideoClip, AudioPart, MusicClip, TitleClip or null.</summary>
    public object? Selected
    {
        get => _selected;
        set { if (!Equals(_selected, value)) { _selected = value; InvalidateVisual(); SelectedRowChanged?.Invoke(SelectedRow); } }
    }

    /// <summary>The track row that holds the selection (for highlighting the track header).</summary>
    public Row SelectedRow => _selected switch
    {
        VideoClip => Row.Video,
        AudioPart => Row.Audio,
        MusicClip => Row.Music,
        TitleClip => Row.Titles,
        _ => Row.None,
    };

    private object? _hover;

    /// <summary>Thumbnail for a media path, or null while it is loading.</summary>
    public Func<string, ImageSource?>? Thumbnail { get; set; }

    /// <summary>Audio peaks (100/s) for a media path, or null while loading.</summary>
    public Func<string, float[]?>? Waveform { get; set; }

    /// <summary>True when the clip has audio clean-up on (shown as a badge).</summary>
    public Func<IAudioClip, bool>? IsCleaned { get; set; }

    /// <summary>Drop marker while dragging media over the timeline (seconds), or null.</summary>
    public double? DropTime { get => _dropTime; set { _dropTime = value; InvalidateVisual(); } }
    private double? _dropTime;

    public event Action<double>? SeekRequested;
    public event Action<object?>? SelectionChanged;
    public event Action<Row>? SelectedRowChanged;
    /// <summary>Raised once before a drag changes the project (record undo here).</summary>
    public event Action<string>? EditStarting;
    /// <summary>Raised after a drag that changed the project.</summary>
    public event Action? EditCompleted;

    private enum DragMode { None, Scrub, TrimLeft, TrimRight, Move }
    private DragMode _mode;
    private object? _dragClip;
    private Point _downAt;
    private bool _dragStarted;
    private double _origStart, _origIn, _origOut, _origDuration, _dragX;
    private int _dropIndex = -1;

    private readonly Typeface _face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private readonly Typeface _mono = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public TimelineControl()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        // Taking keyboard focus on click makes the parent ScrollViewer "bring into view" this very wide
        // element, which jumped the clips sideways under the mouse so the next click missed (or hit empty
        // space and deselected). Scrolling is handled explicitly instead.
        RequestBringIntoView += (_, e) => e.Handled = true;
        SetResourceReference(BgBrushProperty, "TimelineBg");
        SetResourceReference(RowBrushProperty, "TrackHeader");
        SetResourceReference(RowSelectedBrushProperty, "Selected");
        SetResourceReference(LineBrushProperty, "Line");
        SetResourceReference(TickBrushProperty, "LineStrong");
        SetResourceReference(LabelBrushProperty, "TextDim");
        SetResourceReference(VideoBrushProperty, "ClipVideo");
        SetResourceReference(AudioBrushProperty, "ClipAudio");
        SetResourceReference(MusicBrushProperty, "ClipMusic");
        SetResourceReference(TitleBrushProperty, "ClipTitle");
        SetResourceReference(ClipTextBrushProperty, "ClipText");
        SetResourceReference(AccentBrushProperty, "Accent");
        SetResourceReference(SelectBrushProperty, "Text");
        SetResourceReference(PlayheadBrushProperty, "Playhead");
        SetResourceReference(ScrimBrushProperty, "Scrim");
    }

    public double XAt(double seconds) => seconds * PixelsPerSecond + 12;
    public double TimeAtX(double x) => Math.Max(0, (x - 12) / PixelsPerSecond);

    public static Row RowAt(double y) =>
        y < RulerH ? Row.Ruler
        : y >= TitleY && y < TitleY + TitleH ? Row.Titles
        : y >= VideoY && y < VideoY + VideoH ? Row.Video
        : y >= AudioY && y < AudioY + AudioH ? Row.Audio
        : y >= MusicY && y < MusicY + MusicH ? Row.Music
        : Row.None;

    protected override Size MeasureOverride(Size availableSize)
    {
        var extent = (_project?.Extent ?? 0) + 30;
        var width = XAt(extent) + 40;
        var viewport = FindScrollViewer()?.ViewportWidth ?? 0;
        return new Size(Math.Max(width, viewport), TotalHeight);
    }

    private ScrollViewer? FindScrollViewer()
    {
        DependencyObject? p = VisualTreeHelper.GetParent(this);
        while (p != null && p is not ScrollViewer) p = VisualTreeHelper.GetParent(p);
        return p as ScrollViewer;
    }

    /// <summary>The horizontally visible part (in control coordinates), so long timelines draw quickly.</summary>
    private (double Left, double Right) Visible()
    {
        var sv = FindScrollViewer();
        if (sv == null || sv.ViewportWidth <= 0) return (0, ActualWidth);
        return (Math.Max(0, sv.HorizontalOffset - 20), Math.Min(ActualWidth, sv.HorizontalOffset + sv.ViewportWidth + 20));
    }

    // ---------- rendering ----------

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var (left, right) = Visible();
        dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, TotalHeight));
        var selRow = SelectedRow;
        foreach (var (row, y, h) in new[] { (Row.Titles, TitleY, TitleH), (Row.Video, VideoY, VideoH), (Row.Audio, AudioY, AudioH), (Row.Music, MusicY, MusicH) })
            dc.DrawRectangle(row == selRow ? RowSelectedBrush : RowBrush, null, new Rect(0, y, w, h));
        DrawRuler(dc, left, right);

        var p = _project;
        if (p != null)
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            foreach (var t in p.Titles) if (InView(t.Start, t.Duration, left, right)) DrawTitle(dc, t, dpi);
            foreach (var v in p.Video) if (InView(v.Start, v.Duration, left, right)) DrawVideo(dc, v, dpi, left, right);
            foreach (var m in p.Music) if (InView(m.Start, m.Duration, left, right)) DrawMusic(dc, m, dpi, left, right);
            if (_mode == DragMode.Move && _dragStarted && _dragClip is VideoClip vc)
                DrawVideoGhost(dc, vc);
        }

        if (_dropTime is double dt)
        {
            var x = XAt(dt);
            dc.DrawRectangle(AccentBrush, null, new Rect(x - 1.5, RulerH, 3, TotalHeight - RulerH));
        }

        // playhead
        var px = Math.Round(XAt(Playhead)) + 0.5;
        var pen = new Pen(PlayheadBrush, 1.5);
        dc.DrawLine(pen, new Point(px, 0), new Point(px, TotalHeight));
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(new Point(px - 6, 0), true, true);
            g.LineTo(new Point(px + 6, 0), true, false);
            g.LineTo(new Point(px + 6, 8), true, false);
            g.LineTo(new Point(px, 14), true, false);
            g.LineTo(new Point(px - 6, 8), true, false);
        }
        dc.DrawGeometry(PlayheadBrush, null, head);
    }

    private bool InView(double start, double duration, double left, double right) =>
        XAt(start + duration) >= left && XAt(start) <= right;

    private static readonly double[] Steps = { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200 };

    private void DrawRuler(DrawingContext dc, double left, double right)
    {
        var pps = PixelsPerSecond;
        var step = Steps.FirstOrDefault(s => s * pps >= 80, 1200);
        var minor = step / (step >= 1 && step <= 2 ? 4 : 5);
        var tickPen = new Pen(TickBrush, 1);
        var linePen = new Pen(LineBrush, 1);
        dc.DrawLine(linePen, new Point(0, RulerH - 0.5), new Point(ActualWidth, RulerH - 0.5));
        var end = TimeAtX(right);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var first = Math.Floor(TimeAtX(left) / step) * step;
        for (var i = 0; ; i++)
        {
            var t = first + i * minor;
            if (t > end) break;
            var x = Math.Round(XAt(t)) + 0.5;
            var major = Math.Abs(t / step - Math.Round(t / step)) < 1e-6;
            dc.DrawLine(tickPen, new Point(x, major ? RulerH - 10 : RulerH - 5), new Point(x, RulerH));
            if (major)
            {
                var label = new FormattedText(Label(t, step), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _mono, 10, LabelBrush, dpi);
                dc.DrawText(label, new Point(x + 3, 3));
            }
        }
    }

    private static string Label(double t, double step)
    {
        var ts = TimeSpan.FromSeconds(Math.Round(t, 2));
        var head = ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}";
        return step < 1 ? head + "." + (ts.Milliseconds / 100) : head;
    }

    private Rect ClipRect(double start, double duration, double y, double h)
    {
        var x1 = XAt(start);
        var x2 = XAt(start + duration);
        return new Rect(x1 + 0.5, y + 2, Math.Max(2, x2 - x1 - 1), h - 4);
    }

    private void FillClip(DrawingContext dc, Rect r, Brush fill, double opacity = 1)
    {
        if (opacity < 1) dc.PushOpacity(opacity);
        dc.DrawRoundedRectangle(fill, null, r, 4, 4);
        if (opacity < 1) dc.Pop();
    }

    /// <summary>Selection: thick outline in the text colour with an inner accent line. Hover: thin outline.</summary>
    private void DrawOutline(DrawingContext dc, Rect r, bool selected, bool hover)
    {
        if (selected)
        {
            var outer = new Rect(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2);
            dc.DrawRoundedRectangle(null, new Pen(SelectBrush, 3), outer, 5, 5);
            var inner = new Rect(r.X + 1.5, r.Y + 1.5, Math.Max(0, r.Width - 3), Math.Max(0, r.Height - 3));
            dc.DrawRoundedRectangle(null, new Pen(AccentBrush, 1.5), inner, 3, 3);
        }
        else if (hover)
        {
            dc.PushOpacity(0.6);
            dc.DrawRoundedRectangle(null, new Pen(SelectBrush, 1.5), r, 4, 4);
            dc.Pop();
        }
    }

    private void DrawLabel(DrawingContext dc, Rect r, string text, double dpi, double size = 11, double top = 0, double left = 0, Brush? brush = null)
    {
        var visibleLeft = Math.Max(r.X, left);
        var width = r.Right - visibleLeft;
        if (width < 24) return;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _face, size, brush ?? ClipTextBrush, dpi)
        {
            MaxTextWidth = Math.Max(1, width - 12),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(ft, new Point(visibleLeft + 6, r.Y + (top > 0 ? top : (r.Height - ft.Height) / 2)));
    }

    private void DrawFades(DrawingContext dc, Rect r, double fadeIn, double fadeOut)
    {
        var brush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
        brush.Freeze();
        void Tri(Point a, Point b, Point c)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open()) { ctx.BeginFigure(a, true, true); ctx.LineTo(b, true, false); ctx.LineTo(c, true, false); }
            g.Freeze();
            dc.DrawGeometry(brush, null, g);
        }
        var fi = Math.Min(r.Width / 2, fadeIn * PixelsPerSecond);
        var fo = Math.Min(r.Width / 2, fadeOut * PixelsPerSecond);
        if (fi > 1) Tri(r.TopLeft, new Point(r.Left + fi, r.Top), r.BottomLeft);
        if (fo > 1) Tri(r.TopRight, new Point(r.Right - fo, r.Top), r.BottomRight);
    }

    /// <summary>
    /// Real waveform of source range [srcIn, srcOut] drawn across <paramref name="r"/> (mirrored around the middle),
    /// only for the visible columns. Flat line while the peaks load.
    /// </summary>
    private void DrawWave(DrawingContext dc, Rect r, string path, double srcIn, double srcOut, double gain, bool muted, double left, double right)
    {
        var mid = r.Y + r.Height / 2;
        var half = r.Height / 2 - 3;
        var peaks = Waveform?.Invoke(path);
        var x0 = Math.Max(r.X + 2, left);
        var x1 = Math.Min(r.Right - 2, right);
        if (x1 <= x0) return;
        dc.PushOpacity(muted ? 0.25 : 0.6);
        if (peaks == null || peaks.Length == 0)
        {
            dc.DrawLine(new Pen(ClipTextBrush, 1), new Point(x0, mid), new Point(x1, mid));
            dc.Pop();
            return;
        }
        var secondsPerPx = (srcOut - srcIn) / Math.Max(1, r.Width);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            var top = new List<Point>();
            const double stepPx = 2;
            for (var x = x0; x <= x1; x += stepPx)
            {
                var s0 = srcIn + (x - r.X) * secondsPerPx;
                var s1 = s0 + stepPx * secondsPerPx;
                var i0 = Math.Max(0, (int)(s0 * Waveforms.PeaksPerSecond));
                var i1 = Math.Min(peaks.Length - 1, (int)Math.Ceiling(s1 * Waveforms.PeaksPerSecond));
                double max = 0;
                for (var i = i0; i <= i1; i++) if (peaks[i] > max) max = peaks[i];
                // dB scale (−54..0 dBFS → 0..1), like other editors: quiet speech stays readable.
                var level = max * gain;
                var norm = level <= 0 ? 0 : Math.Clamp((20 * Math.Log10(level) + 54) / 54, 0, 1);
                var amp = Math.Max(0.6, norm * half);
                top.Add(new Point(x, mid - amp));
            }
            if (top.Count > 1)
            {
                ctx.BeginFigure(top[0], true, true);
                for (var i = 1; i < top.Count; i++) ctx.LineTo(top[i], true, false);
                for (var i = top.Count - 1; i >= 0; i--) ctx.LineTo(new Point(top[i].X, 2 * mid - top[i].Y), true, false);
            }
        }
        g.Freeze();
        dc.DrawGeometry(ClipTextBrush, null, g);
        dc.Pop();
    }

    private void DrawBadge(DrawingContext dc, Rect r, string text, double dpi)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _face, 9.5, ClipTextBrush, dpi);
        var w = ft.Width + 10;
        if (r.Width < w + 40) return;
        var box = new Rect(r.Right - w - 4, r.Y + 3, w, ft.Height + 2);
        dc.DrawRoundedRectangle(ScrimBrush, null, box, 3, 3);
        dc.DrawText(ft, new Point(box.X + 5, box.Y + 1));
    }

    private void DrawVideo(DrawingContext dc, VideoClip c, double dpi, double left, double right)
    {
        var r = ClipRect(c.Start, c.Duration, VideoY, VideoH);
        var selected = _selected is VideoClip sv && ReferenceEquals(sv, c);
        var dragging = _mode == DragMode.Move && _dragStarted && ReferenceEquals(_dragClip, c);
        FillClip(dc, r, VideoBrush, dragging ? 0.35 : 1);

        if (!dragging && Thumbnail?.Invoke(c.Path) is { } thumb && r.Width > 8)
        {
            dc.PushClip(new RectangleGeometry(new Rect(r.X + 2, r.Y + 2, Math.Max(0, r.Width - 4), Math.Max(0, r.Height - 4)), 3, 3));
            var th = r.Height - 4;
            var tw = thumb.Height > 0 ? th * thumb.Width / thumb.Height : th * 16 / 9;
            var startX = r.X + 2;
            if (left > startX) startX += Math.Floor((left - startX) / tw) * tw;
            for (var x = startX; x < Math.Min(r.Right - 2, right); x += tw)
                dc.DrawImage(thumb, new Rect(x, r.Y + 2, tw, th));
            dc.DrawRectangle(ScrimBrush, null, new Rect(r.X, r.Y, r.Width, 18));
            dc.Pop();
        }
        var label = c.Name + (Math.Abs(c.Speed - 1) > 0.001 ? $"  {c.Speed:0.##}×" : "");
        DrawLabel(dc, r, label, dpi, 11, 2, left);
        DrawFades(dc, r, c.FadeIn, c.FadeOut);
        DrawOutline(dc, r, selected, !selected && ReferenceEquals(_hover, c));

        // linked audio (its own selectable part)
        if (c.HasAudio && !c.IsImage)
        {
            var a = ClipRect(c.Start, c.Duration, AudioY, AudioH);
            var audioSelected = _selected is AudioPart ap && ReferenceEquals(ap.Clip, c);
            var audioHover = _hover is AudioPart hp && ReferenceEquals(hp.Clip, c);
            FillClip(dc, a, AudioBrush, c.Muted ? 0.3 : (dragging ? 0.35 : 0.9));
            DrawWave(dc, a, c.Path, c.In, c.Out, c.Volume, c.Muted, left, right);
            // On the faded (removed) clip, white text vanishes in light mode: use the theme's text colour.
            if (c.Muted) DrawLabel(dc, a, "Audio removed", dpi, 10.5, 0, left, SelectBrush);
            else if (IsCleaned?.Invoke(c) == true) DrawBadge(dc, a, "Cleaned", dpi);
            DrawOutline(dc, a, audioSelected, !audioSelected && audioHover);
        }
    }

    private void DrawVideoGhost(DrawingContext dc, VideoClip c)
    {
        var w = c.Duration * PixelsPerSecond;
        var r = new Rect(_dragX - w / 2, VideoY + 1, Math.Max(2, w), VideoH - 2);
        dc.PushOpacity(0.8);
        dc.DrawRoundedRectangle(VideoBrush, new Pen(AccentBrush, 2), r, 4, 4);
        dc.Pop();
        if (_dropIndex >= 0 && _project != null)
        {
            var t = _dropIndex < _project.Video.Count ? _project.Video[_dropIndex].Start : _project.Duration;
            var x = XAt(t);
            dc.DrawRectangle(AccentBrush, null, new Rect(x - 1.5, VideoY - 3, 3, VideoH + AudioH + 8));
        }
    }

    private void DrawMusic(DrawingContext dc, MusicClip m, double dpi, double left, double right)
    {
        var r = ClipRect(m.Start, m.Duration, MusicY, MusicH);
        var selected = ReferenceEquals(_selected, m);
        FillClip(dc, r, MusicBrush, m.Muted ? 0.35 : 1);
        DrawWave(dc, r, m.Path, m.In, m.Out, m.Volume, m.Muted, left, right);
        DrawFades(dc, r, m.FadeIn, m.FadeOut);
        DrawLabel(dc, r, (m.Muted ? "Muted · " : "♪ ") + m.Name, dpi, 11, 2, left, m.Muted ? SelectBrush : null);
        if (!m.Muted && IsCleaned?.Invoke(m) == true) DrawBadge(dc, r, "Cleaned", dpi);
        DrawOutline(dc, r, selected, !selected && ReferenceEquals(_hover, m));
    }

    private void DrawTitle(DrawingContext dc, TitleClip t, double dpi)
    {
        var r = ClipRect(t.Start, t.Duration, TitleY, TitleH);
        var selected = ReferenceEquals(_selected, t);
        FillClip(dc, r, TitleBrush);
        DrawLabel(dc, r, "T  " + t.Text.Replace('\n', ' '), dpi);
        DrawOutline(dc, r, selected, !selected && ReferenceEquals(_hover, t));
    }

    // ---------- hit testing ----------

    /// <summary>
    /// What is under <paramref name="p"/>: the object to select (AudioPart on the A1 row), the clip a drag
    /// changes (the video clip for its audio part), and the drag mode.
    /// </summary>
    private (object? select, object? drag, DragMode mode) HitTest(Point p)
    {
        if (_project == null) return (null, null, DragMode.None);
        var row = RowAt(p.Y);
        IEnumerable<(object clip, double start, double dur)> items = row switch
        {
            Row.Titles => _project.Titles.Select(t => ((object)t, t.Start, t.Duration)).Reverse(),
            Row.Video => _project.Video.Select(v => ((object)v, v.Start, v.Duration)),
            Row.Audio => _project.Video.Where(v => v.HasAudio && !v.IsImage).Select(v => ((object)v, v.Start, v.Duration)),
            Row.Music => _project.Music.Select(m => ((object)m, m.Start, m.Duration)).Reverse(),
            _ => Enumerable.Empty<(object, double, double)>(),
        };
        foreach (var (clip, start, dur) in items)
        {
            var x1 = XAt(start);
            var x2 = XAt(start + dur);
            if (p.X < x1 - 2 || p.X > x2 + 2) continue;
            var select = row == Row.Audio ? new AudioPart((VideoClip)clip) : clip;
            var grab = Math.Min(EdgeGrab, (x2 - x1) / 3);
            if (p.X - x1 <= grab) return (select, clip, DragMode.TrimLeft);
            if (x2 - p.X <= grab) return (select, clip, DragMode.TrimRight);
            return (select, clip, DragMode.Move);
        }
        return (null, null, DragMode.None);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_mode == DragMode.None)
        {
            var (hit, _, mode) = HitTest(p);
            Cursor = mode is DragMode.TrimLeft or DragMode.TrimRight ? Cursors.SizeWE
                : hit != null ? Cursors.Hand
                : RowAt(p.Y) == Row.Ruler ? Cursors.IBeam : null;
            if (!Equals(hit, _hover))
            {
                _hover = hit;
                InvalidateVisual();
            }
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag(commit: true);
            return;
        }
        if (_mode == DragMode.Scrub)
        {
            SeekRequested?.Invoke(TimeAtX(p.X));
            return;
        }
        if (!_dragStarted)
        {
            if (Math.Abs(p.X - _downAt.X) < DragThreshold) return;
            _dragStarted = true;
            EditStarting?.Invoke("drag");
        }
        ApplyDrag(p);
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != null)
        {
            _hover = null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
        Focus();
        // A press that arrives while a drag is already running (duplicate input, or a second button
        // event before the release) must not restart the gesture.
        if (_mode != DragMode.None) return;
        var p = e.GetPosition(this);
        _downAt = p;
        _dragStarted = false;
        if (RowAt(p.Y) == Row.Ruler || _project == null)
        {
            _mode = DragMode.Scrub;
            CaptureMouse();
            SeekRequested?.Invoke(TimeAtX(p.X));
            return;
        }

        var (select, drag, mode) = HitTest(p);
        if (!Equals(select, _selected))
        {
            Selected = select;
            SelectionChanged?.Invoke(select);
        }
        if (select == null)
        {
            // Empty track area: deselect and move the playhead.
            _mode = DragMode.Scrub;
            CaptureMouse();
            SeekRequested?.Invoke(TimeAtX(p.X));
            return;
        }
        // On a clip: select it without moving the playhead; a drag then trims or moves it.
        _dragClip = drag;
        _mode = mode;
        switch (drag)
        {
            case VideoClip v: _origStart = v.Start; _origIn = v.In; _origOut = v.Out; break;
            case MusicClip m: _origStart = m.Start; _origIn = m.In; _origOut = m.Out; break;
            case TitleClip t: _origStart = t.Start; _origDuration = t.Duration; break;
        }
        _dragX = p.X;
        CaptureMouse();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_mode != DragMode.None) EndDrag(commit: true);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_mode != DragMode.None) EndDrag(commit: true);
    }

    private void EndDrag(bool commit)
    {
        var mode = _mode;
        var started = _dragStarted;
        _mode = DragMode.None;
        _dragStarted = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (mode == DragMode.Move && started && _dragClip is VideoClip v && _project != null && commit && _dropIndex >= 0)
        {
            var from = _project.Video.IndexOf(v);
            var to = _dropIndex > from ? _dropIndex - 1 : _dropIndex;
            if (from >= 0 && to != from)
            {
                _project.Video.RemoveAt(from);
                _project.Video.Insert(Math.Clamp(to, 0, _project.Video.Count), v);
            }
        }
        _dropIndex = -1;
        _dragClip = null;
        _project?.Layout();
        InvalidateMeasure();
        InvalidateVisual();
        if (started && mode != DragMode.Scrub)
            EditCompleted?.Invoke();
    }

    // ---------- drag edits ----------

    private IEnumerable<double> SnapTargets(object exclude)
    {
        var p = _project!;
        yield return 0;
        yield return Playhead;
        foreach (var v in p.Video) { if (!ReferenceEquals(v, exclude)) { yield return v.Start; yield return v.End; } }
        foreach (var m in p.Music) { if (!ReferenceEquals(m, exclude)) { yield return m.Start; yield return m.End; } }
        foreach (var t in p.Titles) { if (!ReferenceEquals(t, exclude)) { yield return t.Start; yield return t.End; } }
    }

    /// <summary>Snaps a time to the nearest edge or the playhead within a few pixels (hold Alt to disable).</summary>
    private double Snap(double t, object exclude)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return t;
        var best = t;
        var bestPx = SnapPx;
        foreach (var target in SnapTargets(exclude))
        {
            var d = Math.Abs(target - t) * PixelsPerSecond;
            if (d < bestPx) { bestPx = d; best = target; }
        }
        return best;
    }

    private void ApplyDrag(Point p)
    {
        var dt = (p.X - _downAt.X) / PixelsPerSecond;
        switch (_dragClip)
        {
            case VideoClip v when _mode == DragMode.TrimLeft:
            {
                // Ripple trim: the clip keeps its start, so the source in-point moves.
                var newIn = Math.Clamp(_origIn + dt * v.Speed, 0, _origOut - 0.1 * v.Speed);
                v.In = v.IsImage ? 0 : newIn;
                if (v.IsImage) v.Out = Math.Max(0.1, _origOut - dt);
                break;
            }
            case VideoClip v when _mode == DragMode.TrimRight:
            {
                var end = Snap(_origStart + (_origOut - _origIn) / v.Speed + dt, v);
                var newOut = _origIn + (end - _origStart) * v.Speed;
                v.Out = Math.Clamp(newOut, v.In + 0.1 * v.Speed, v.IsImage ? 3600 : v.SourceDuration);
                break;
            }
            case VideoClip:
            {
                _dragX = p.X;
                var t = TimeAtX(p.X);
                var list = _project!.Video;
                _dropIndex = list.Count;
                for (var i = 0; i < list.Count; i++)
                    if (t < list[i].Start + list[i].Duration / 2) { _dropIndex = i; break; }
                break;
            }
            case MusicClip m when _mode == DragMode.TrimLeft:
            {
                var start = Snap(_origStart + dt, m);
                var shift = Math.Clamp(start - _origStart, -_origIn, _origOut - 0.1 - _origIn);
                shift = Math.Max(shift, -_origStart);
                m.In = _origIn + shift;
                m.Start = _origStart + shift;
                break;
            }
            case MusicClip m when _mode == DragMode.TrimRight:
            {
                var end = Snap(_origStart + (_origOut - _origIn) + dt, m);
                m.Out = Math.Clamp(_origIn + (end - _origStart), m.In + 0.1, m.SourceDuration);
                break;
            }
            case MusicClip m:
            {
                var len = m.Duration;
                var start = Math.Max(0, _origStart + dt);
                var snappedStart = Snap(start, m);
                var snappedEnd = Snap(start + len, m) - len;
                m.Start = Math.Max(0, Math.Abs(snappedStart - start) <= Math.Abs(snappedEnd - start) ? snappedStart : snappedEnd);
                break;
            }
            case TitleClip t when _mode == DragMode.TrimLeft:
            {
                var end = _origStart + _origDuration;
                var start = Math.Clamp(Snap(_origStart + dt, t), 0, end - 0.2);
                t.Start = start;
                t.Duration = end - start;
                break;
            }
            case TitleClip t when _mode == DragMode.TrimRight:
            {
                var end = Snap(_origStart + _origDuration + dt, t);
                t.Duration = Math.Max(0.2, end - t.Start);
                break;
            }
            case TitleClip t:
            {
                var start = Math.Max(0, _origStart + dt);
                var snappedStart = Snap(start, t);
                var snappedEnd = Snap(start + t.Duration, t) - t.Duration;
                t.Start = Math.Max(0, Math.Abs(snappedStart - start) <= Math.Abs(snappedEnd - start) ? snappedStart : snappedEnd);
                break;
            }
        }
        _project?.Layout();
    }
}
