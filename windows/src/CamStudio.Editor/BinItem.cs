using System.ComponentModel;
using System.Windows.Media;
using CamStudio.Editor.Model;

namespace CamStudio.Editor;

/// <summary>One row in the media bin.</summary>
public sealed class BinItem : INotifyPropertyChanged
{
    public BinItem(MediaAsset asset) => Asset = asset;

    public MediaAsset Asset { get; }
    public string Path => Asset.Path;
    public string Name => Asset.Name;

    public string Glyph => Asset.IsImage ? "" : Asset.IsAudioOnly ? "" : "";

    public string Detail =>
        Asset.IsImage ? $"Image · {Asset.Width}×{Asset.Height}"
        : Asset.IsAudioOnly ? $"Audio · {EditorView.FormatShort(Asset.Duration)}"
        : $"{EditorView.FormatShort(Asset.Duration)} · {Asset.Width}×{Asset.Height}{(Asset.HasAudio ? "" : " · no sound")}";

    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
