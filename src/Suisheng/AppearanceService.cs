using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Suisheng;

public sealed record AvatarAssets(string SourcePath, string OrbPath);

public static class AppearanceService
{
    public static BitmapFrame LoadImage(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) && !Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) && !Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("仅支持 PNG、JPG 或 JPEG 静态图片。");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || info.Length > 10 * 1024 * 1024) throw new InvalidDataException("图片不存在、为空或超过 10 MB。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
        if (decoder is not (PngBitmapDecoder or JpegBitmapDecoder) || decoder.Frames.Count != 1)
            throw new InvalidDataException("仅支持单帧 PNG、JPG 或 JPEG 静态图片，不支持 GIF 等格式。");
        var frame = decoder.Frames[0];
        if (frame.PixelWidth is < 16 or > 4096 || frame.PixelHeight is < 16 or > 4096 || (long)frame.PixelWidth * frame.PixelHeight > 16_000_000)
            throw new InvalidDataException("图片宽高须为 16–4096 像素，且总像素不超过 1600 万。");
        frame.Freeze();
        return frame;
    }

    public static BitmapSource? TryLoadImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return LoadImage(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidDataException or FileFormatException) { return null; }
    }

    public static Rect CropRectangle(BitmapSource image, double zoom, double offsetX, double offsetY)
    {
        double side = Math.Min(image.PixelWidth, image.PixelHeight) / zoom;
        double width = side / image.PixelWidth, height = side / image.PixelHeight;
        double x = Math.Clamp(0.5 + offsetX * (1 - width) / 2 - width / 2, 0, 1 - width);
        double y = Math.Clamp(0.5 + offsetY * (1 - height) / 2 - height / 2, 0, 1 - height);
        return new Rect(x, y, width, height);
    }

    public static ImageBrush CreateBrush(BitmapSource image, double zoom, double offsetX, double offsetY) =>
        new() { ImageSource = image, Stretch = Stretch.UniformToFill, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Viewbox = CropRectangle(image, zoom, offsetX, offsetY) };

    public static BitmapSource Crop(BitmapSource image, double zoom, double offsetX, double offsetY)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawRectangle(CreateBrush(image, zoom, offsetX, offsetY), null, new Rect(0, 0, 256, 256));
        var bitmap = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static AvatarAssets Import(string source, double zoom, double offsetX, double offsetY, string? root = null)
    {
        var image = LoadImage(source);
        string directory = Path.Combine(root ?? SettingsStore.DataDirectory, "images");
        Directory.CreateDirectory(directory);
        string id = Guid.NewGuid().ToString("N");
        string sourcePath = Path.Combine(directory, id + "-source.png");
        string orbPath = Path.Combine(directory, id + "-orb.png");
        try
        {
            Save(image, sourcePath);
            Save(Crop(image, zoom, offsetX, offsetY), orbPath);
            return new(sourcePath, orbPath);
        }
        finally
        {
            DeleteTemporary(sourcePath);
            DeleteTemporary(orbPath);
        }
    }

    private static void Save(BitmapSource image, string path)
    {
        string temporary = path + ".new";
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(temporary)) encoder.Save(stream);
        File.Move(temporary, path);
    }

    private static void DeleteTemporary(string path)
    {
        string temporary = path + ".new";
        if (File.Exists(temporary)) File.Delete(temporary);
    }
}
