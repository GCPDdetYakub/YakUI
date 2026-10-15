using System.Buffers.Binary;

namespace YakUI.Core.Icons;

public enum IconFormat
{
    Png,
    Jpeg,
    Gif,
    Bmp,
    WebP,
    Ico,
}

public sealed record IconCheck(bool IsValid, IconFormat? Format, int Width, int Height, string? Problem)
{
    public string Extension => Format == IconFormat.Jpeg ? "jpg" : "png";

    /// <summary>GIF, BMP, WebP, ICO хранятся в кэше как PNG (FR-34c).</summary>
    public bool NeedsConversion => Format is IconFormat.Gif or IconFormat.Bmp or IconFormat.WebP or IconFormat.Ico;
}

/// <summary>
/// Проверка картинки перед показом (FR-34c): PNG, JPEG, GIF, BMP или WebP по сигнатуре, файл ≤ 1 МБ, стороны ≤ 4096 px
/// (защита от «бомб» при декодировании). GIF/BMP/WebP конвертируются в PNG, картинки больше <see cref="CacheSide"/> уменьшаются.
/// Размеры читаются из заголовка, без декодирования изображения.
/// </summary>
public static class IconValidator
{
    public const int MaxBytes = 1024 * 1024;
    public const int MaxSide = 4096;

    /// <summary>Больше этого по любой стороне — уменьшается до него с сохранением пропорций (FR-34c).</summary>
    public const int CacheSide = 256;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static IconCheck Check(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return Fail("пустой файл");
        if (data.Length > MaxBytes)
            return Fail($"файл больше {MaxBytes / 1024} КБ");

        IconFormat format;
        int width, height;
        if (data.StartsWith(PngSignature))
        {
            // IHDR сразу после сигнатуры: длина(4) "IHDR"(4) ширина(4) высота(4).
            if (data.Length < 24 || !data.Slice(12, 4).SequenceEqual("IHDR"u8))
                return Fail("повреждён заголовок PNG");
            format = IconFormat.Png;
            width = (int)BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
            height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
        }
        else if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            if (!TryJpegSize(data, out width, out height))
                return Fail("не найден размер JPEG (SOF)");
            format = IconFormat.Jpeg;
        }
        else if (data.Length >= 10 && (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)))
        {
            // Logical Screen Descriptor: ширина и высота, little-endian.
            format = IconFormat.Gif;
            width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        }
        else if (data.Length >= 26 && data[0] == (byte)'B' && data[1] == (byte)'M')
        {
            // BITMAPINFOHEADER: ширина и высота int32; отрицательная высота — изображение сверху вниз.
            format = IconFormat.Bmp;
            width = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[18..]));
            height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[22..]));
        }
        else if (data.Length >= 30 && data.StartsWith("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            if (!TryWebPSize(data, out width, out height))
                return Fail("не найден размер WebP");
            format = IconFormat.WebP;
        }
        else if (data.Length >= 22 && data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0)
        {
            if (!TryIcoSize(data, out width, out height))
                return Fail("повреждена таблица образов ICO");
            format = IconFormat.Ico;
        }
        else
        {
            return Fail("не PNG, JPEG, GIF, BMP, WebP или ICO");
        }

        if (width <= 0 || height <= 0)
            return Fail("нулевой размер");
        if (width > MaxSide || height > MaxSide)
            return new IconCheck(false, format, width, height, $"размер {width}×{height} больше {MaxSide} px");
        return new IconCheck(true, format, width, height, null);

        static IconCheck Fail(string why) => new(false, null, 0, 0, why);
    }

    /// <summary>
    /// ICO (v0.2.13): таблица образов по 16 байт с шестого байта; ширина и высота — по байту, 0 означает 256.
    /// Берётся самый крупный образ — его же выбирает декодер.
    /// </summary>
    private static bool TryIcoSize(ReadOnlySpan<byte> d, out int width, out int height)
    {
        width = height = 0;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(d[4..]);
        if (count == 0 || 6 + count * 16 > d.Length)
            return false;
        for (var i = 0; i < count; i++)
        {
            var e = d.Slice(6 + i * 16, 16);
            var w = e[0] == 0 ? 256 : e[0];
            var h = e[1] == 0 ? 256 : e[1];
            if (w * h > width * height)
                (width, height) = (w, h);
        }

        return true;
    }

    /// <summary>Размер WebP из чанка VP8 (с потерями), VP8L (без потерь) или VP8X (расширенный).</summary>
    private static bool TryWebPSize(ReadOnlySpan<byte> d, out int width, out int height)
    {
        width = height = 0;
        var chunk = d.Slice(12, 4);
        if (chunk.SequenceEqual("VP8X"u8))
        {
            // 24-битные «ширина − 1» и «высота − 1» по смещениям 24 и 27.
            width = 1 + (d[24] | d[25] << 8 | d[26] << 16);
            height = 1 + (d[27] | d[28] << 8 | d[29] << 16);
            return true;
        }

        if (chunk.SequenceEqual("VP8L"u8))
        {
            // Сигнатура 0x2F, затем 14 бит «ширина − 1» и 14 бит «высота − 1».
            if (d[20] != 0x2F)
                return false;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(d[21..]);
            width = 1 + (int)(bits & 0x3FFF);
            height = 1 + (int)((bits >> 14) & 0x3FFF);
            return true;
        }

        if (chunk.SequenceEqual("VP8 "u8))
        {
            // Кадр-ключ: стартовый код 9D 01 2A, затем 14-битные ширина и высота.
            if (d[23] != 0x9D || d[24] != 0x01 || d[25] != 0x2A)
                return false;
            width = BinaryPrimitives.ReadUInt16LittleEndian(d[26..]) & 0x3FFF;
            height = BinaryPrimitives.ReadUInt16LittleEndian(d[28..]) & 0x3FFF;
            return true;
        }

        return false;
    }

    private static bool TryJpegSize(ReadOnlySpan<byte> d, out int width, out int height)
    {
        width = height = 0;
        var i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF)
            {
                i++;
                continue;
            }

            var marker = d[i + 1];
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7) or 0xFF)
            {
                i += marker == 0xFF ? 1 : 2;
                continue;
            }

            var len = BinaryPrimitives.ReadUInt16BigEndian(d[(i + 2)..]);
            // SOF0…SOF15, кроме DHT (C4), JPG (C8), DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(d[(i + 5)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(d[(i + 7)..]);
                return true;
            }

            if (len < 2)
                return false;
            i += 2 + len;
        }

        return false;
    }
}
