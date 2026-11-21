using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CamStudio.Desktop.Media;

internal enum StudioMicStyle { Mesh, Foam }
internal sealed record StudioLabelStyle(string Font, int Size, bool Bold, int Ink, int Left, int Right);

/// <summary>A cached, premultiplied overlay shared by the local preview and encoded outputs.</summary>
internal sealed class StudioMicrophone(string label, StudioLabelStyle labelStyle, StudioMicStyle style, int size,
    bool followsAudio, bool recording, bool streaming)
{
    private const int Width = 360, Height = 360;
    private sealed record MicPhoto(BitmapSource Normal, BitmapSource Dim, double StemCenter);
    private static readonly Lazy<Raster> Mesh = new(() => DrawMic(LoadPhoto("mesh")));
    private static readonly Lazy<Raster> Foam = new(() => DrawMic(LoadPhoto("foam")));
    private const int LabelWidth = 1280, LabelHeight = 80;
    private sealed record Raster(byte[] Pixels, byte[]? Dim, int Width, int Height);
    private readonly Raster _mic = style == StudioMicStyle.Foam ? Foam.Value : Mesh.Value;
    private readonly Raster? _label = string.IsNullOrWhiteSpace(label) ? null : DrawLabel(label, labelStyle);
    public bool FollowsAudio { get; } = followsAudio;
    public bool Recording { get; } = recording;
    public bool Streaming { get; } = streaming;

    private static MicPhoto LoadPhoto(string name)
    {
        using var stream = typeof(StudioMicrophone).Assembly.GetManifestResourceStream($"CamStudio.Desktop.Assets.studio-mic-{name}.png")
            ?? throw new InvalidOperationException($"Missing studio microphone asset: {name}");
        var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var photo = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int stride = photo.PixelWidth * 4;
        var pixels = new byte[stride * photo.PixelHeight];
        photo.CopyPixels(pixels, stride, 0);
        int left = photo.PixelWidth, top = photo.PixelHeight, right = -1, bottom = -1;
        for (int y = 0; y < photo.PixelHeight; y++)
        for (int x = 0; x < photo.PixelWidth; x++)
        {
            if (pixels[y * stride + x * 4 + 3] <= 20) continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
        }

        // Cut just inside the stem so its silhouette continues through the video edge.
        int photoHeight = bottom - top + 1;
        int cropHeight = photoHeight - Math.Max(1, photoHeight / 16);
        int stemRow = (top + cropHeight - 1) * stride;
        int stemLeft = left, stemRight = right;
        while (stemLeft < right && pixels[stemRow + stemLeft * 4 + 3] < 128) stemLeft++;
        while (stemRight > stemLeft && pixels[stemRow + stemRight * 4 + 3] < 128) stemRight--;
        double stemCenter = (stemLeft + stemRight + 1) / 2d - left;

        var crop = new CroppedBitmap(photo, new Int32Rect(left, top, right - left + 1, cropHeight));
        crop.Freeze();
        int dimStride = crop.PixelWidth * 4;
        var dimPixels = new byte[dimStride * cropHeight];
        crop.CopyPixels(dimPixels, dimStride, 0);
        for (int i = 0; i < dimPixels.Length; i += 4)
        {
            for (int channel = 0; channel < 3; channel++)
                dimPixels[i + channel] = (byte)(dimPixels[i + channel] * 3 / 8);
        }
        var dim = BitmapSource.Create(crop.PixelWidth, cropHeight, crop.DpiX, crop.DpiY,
            PixelFormats.Bgra32, null, dimPixels, dimStride);
        dim.Freeze();
        return new MicPhoto(crop, dim, stemCenter);
    }

    private static Raster DrawMic(MicPhoto asset) =>
        new(DrawMicPhoto(asset, asset.Normal), DrawMicPhoto(asset, asset.Dim), Width, Height);

    private static byte[] DrawMicPhoto(MicPhoto asset, BitmapSource photo)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            double stemCenter = asset.StemCenter;
            double widestSide = Math.Max(stemCenter, photo.PixelWidth - stemCenter);
            double scale = Math.Min((Width / 2d - 8) / widestSide, (Height - 58d) / photo.PixelHeight);
            double photoWidth = photo.PixelWidth * scale, photoHeight = photo.PixelHeight * scale;
            double micTop = Height - photoHeight;
            dc.DrawImage(photo, new Rect(Width / 2d - stemCenter * scale, micTop, photoWidth, photoHeight));
        }
        return Rasterize(visual, Width, Height);
    }

    private static Raster DrawLabel(string label, StudioLabelStyle style)
    {
        var visual = new DrawingVisual();
        static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        var gradient = new LinearGradientBrush(Rgb(style.Left), Rgb(style.Right),
            new Point(0, 0.5), new Point(1, 0.5));
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(gradient, null, new Rect(0, 0, LabelWidth, LabelHeight));
            var text = new FormattedText(label, CultureInfo.CurrentUICulture,
                CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                new Typeface(new FontFamily(style.Font), FontStyles.Normal,
                    style.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
                style.Size == 0 ? 36 : style.Size == 2 ? 60 : 48,
                new SolidColorBrush(Rgb(style.Ink)), 1)
            {
                TextAlignment = TextAlignment.Center,
                MaxTextWidth = LabelWidth - 64,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis
            };
            dc.DrawText(text, new Point(32, (LabelHeight - text.Height) / 2));
        }
        return new Raster(Rasterize(visual, LabelWidth, LabelHeight), null, LabelWidth, LabelHeight);
    }

    private static byte[] Rasterize(DrawingVisual visual, int width, int height)
    {
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        var bytes = new byte[width * height * 4];
        image.CopyPixels(bytes, width * 4, 0);
        return bytes;
    }

    private (int X, int Y, int Width, int Height) MicLayout(int width, int height)
    {
        var shortSide = Math.Min(width, height);
        var side = Math.Max(2, (int)(shortSide * (size == 0 ? .29 : size == 2 ? .51 : .40)) & ~1);
        return CenterBottom(width, height, side, side);
    }

    private static (int X, int Y, int Width, int Height) LabelLayout(int width, int height)
    {
        int bandWidth = Math.Max(2, (int)(width * .72) & ~1);
        int bandHeight = Math.Max(2, Math.Min(bandWidth * LabelHeight / LabelWidth, height / 10) & ~1);
        return CenterBottom(width, height, bandWidth, bandHeight);
    }

    private static (int X, int Y, int Width, int Height) CenterBottom(int width, int height, int layerWidth, int layerHeight)
        => (((width - layerWidth) / 2) & ~1, height - layerHeight, layerWidth, layerHeight);

    // Sample the center of the original scene, including the speaker's face and torso.
    // A symmetric grid keeps mirrored previews and encoded video on the same scale.
    private static int SceneLight(byte[] pixels, int width, int height, bool bgra)
    {
        int sum = 0;
        for (var row = 0; row < 8; row++)
        for (var col = 0; col < 8; col++)
        {
            int x = (int)((width - 1) * (.325 + .05 * col));
            int y = (int)((height - 1) * (.175 + .075 * row));
            int offset = y * width + x;
            if (bgra)
            {
                offset *= 4;
                sum += (25 * pixels[offset] + 129 * pixels[offset + 1] + 66 * pixels[offset + 2]) >> 8;
            }
            else
            {
                sum += Math.Max(0, pixels[offset] - 16);
            }
        }
        // Full photo brightness starts at about 50% video luma. Restrict the range to
        // avoid a disappearing mic on a dark shirt or a glowing one in a dim room.
        return Math.Clamp((sum - 64 * 12) * 256 / (64 * (110 - 12)), 0, 256);
    }

    private static int LitChannel(Raster layer, int channel, int light)
        => layer.Dim is { } dim
            ? dim[channel] + ((layer.Pixels[channel] - dim[channel]) * light >> 8)
            : layer.Pixels[channel];

    public void ApplyPreview(byte[] bgra, int width, int height)
    {
        int light = SceneLight(bgra, width, height, bgra: true);
        BlendPreview(bgra, width, _mic, MicLayout(width, height), light);
        if (_label is { } label) BlendPreview(bgra, width, label, LabelLayout(width, height), 256);
    }

    private static void BlendPreview(byte[] bgra, int width, Raster layer,
        (int X, int Y, int Width, int Height) bounds, int light)
    {
        var (x0, y0, layerWidth, layerHeight) = bounds;
        for (var y = 0; y < layerHeight; y++)
        for (var x = 0; x < layerWidth; x++)
        {
            var src = Pixel(layer, x, y, layerWidth, layerHeight);
            var alpha = layer.Pixels[src + 3];
            if (alpha == 0) continue;
            var dst = ((y0 + y) * width + x0 + x) * 4;
            for (var k = 0; k < 3; k++)
                bgra[dst + k] = (byte)(LitChannel(layer, src + k, light) + bgra[dst + k] * (255 - alpha) / 255);
        }
    }

    private static int Pixel(Raster layer, int x, int y, int width, int height) =>
        ((y * layer.Height / height) * layer.Width + x * layer.Width / width) * 4;

    public Nv12Frame ApplyOutput(Nv12Frame source)
    {
        var frame = new Nv12Frame(source.Width, source.Height) { PtsUs = source.PtsUs };
        Buffer.BlockCopy(source.Data, 0, frame.Data, 0, source.Data.Length);
        int light = SceneLight(source.Data, frame.Width, frame.Height, bgra: false);
        BlendOutput(frame, _mic, MicLayout(frame.Width, frame.Height), light);
        if (_label is { } label) BlendOutput(frame, label, LabelLayout(frame.Width, frame.Height), 256);
        return frame;
    }

    private static void BlendOutput(Nv12Frame frame, Raster layer,
        (int X, int Y, int Width, int Height) bounds, int light)
    {
        var (x0, y0, layerWidth, layerHeight) = bounds;
        for (var y = 0; y < layerHeight; y += 2)
        for (var x = 0; x < layerWidth; x += 2)
        {
            int sumA = 0, sumU = 0, sumV = 0;
            for (var dy = 0; dy < 2; dy++)
            for (var dx = 0; dx < 2; dx++)
            {
                var src = Pixel(layer, x + dx, y + dy, layerWidth, layerHeight);
                var a = layer.Pixels[src + 3];
                if (a == 0) continue;
                int b = LitChannel(layer, src, light), g = LitChannel(layer, src + 1, light), r = LitChannel(layer, src + 2, light);
                var dst = (y0 + y + dy) * frame.Width + x0 + x + dx;
                frame.Data[dst] = (byte)Math.Clamp(16 + ((66 * r + 129 * g + 25 * b) >> 8)
                    + (frame.Data[dst] - 16) * (255 - a) / 255, 0, 255);
                sumA += a;
                sumU += -38 * r - 74 * g + 112 * b;
                sumV += 112 * r - 94 * g - 18 * b;
            }
            if (sumA == 0) continue;
            var uv = frame.Width * frame.Height + (y0 + y) / 2 * frame.Width + x0 + x;
            frame.Data[uv] = (byte)Math.Clamp(128 + sumU / 1024 + (frame.Data[uv] - 128) * (1020 - sumA) / 1020, 0, 255);
            frame.Data[uv + 1] = (byte)Math.Clamp(128 + sumV / 1024 + (frame.Data[uv + 1] - 128) * (1020 - sumA) / 1020, 0, 255);
        }
    }
}
