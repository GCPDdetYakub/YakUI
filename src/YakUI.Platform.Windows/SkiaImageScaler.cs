using SkiaSharp;
using YakUI.Core.Platform;

namespace YakUI.Platform.Windows;

/// <summary>Уменьшение иконок через SkiaSharp (та же версия, что у Avalonia): высокое качество, вывод PNG (FR-34c).</summary>
public sealed class SkiaImageScaler : IImageScaler
{
    public byte[] DownscaleToPng(byte[] image, int maxSide)
    {
        using var source = SKBitmap.Decode(image) ?? throw new InvalidDataException("Картинку не удалось декодировать.");
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var resized = scale < 1.0
            ? source.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul), SKFilterQuality.High)
              ?? throw new InvalidDataException("Не удалось уменьшить картинку.")
            : source.Copy();
        using var img = SKImage.FromBitmap(resized);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
