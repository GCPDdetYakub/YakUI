using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using YakUI.Core.Localization;

namespace YakUI.Core.Archive;

public sealed record ZipEntryInfo(
    string Name,
    long CompressedSize,
    long UncompressedSize,
    long LocalHeaderOffset,
    ushort Method,
    bool IsDirectory);

/// <summary>Ограничения при чтении архива (NFR-04).</summary>
public sealed record ZipLimits
{
    public int MaxEntries { get; init; } = 100_000;

    public long MaxCentralDirectorySize { get; init; } = 32 * 1024 * 1024;

    /// <summary>Максимум распакованного размера одной читаемой записи (манифест, иконка).</summary>
    public long MaxEntryReadSize { get; init; } = 4 * 1024 * 1024;
}

/// <summary>
/// Оглавление ZIP, прочитанное с конца архива (FR-36b): End of Central Directory → (ZIP64 locator → ZIP64 EOCD) →
/// Central Directory. Из записей читаются только нужные (local header + данные), с распаковкой Deflate.
/// </summary>
public sealed class ZipDirectory
{
    private const uint EocdSignature = 0x06054b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint Zip64EocdSignature = 0x06064b50;
    private const uint CentralHeaderSignature = 0x02014b50;
    private const uint LocalHeaderSignature = 0x04034b50;
    private const int EocdMinSize = 22;
    private const int MaxCommentSize = 0xFFFF;

    private readonly IRangeSource _source;
    private readonly ZipLimits _limits;

    private ZipDirectory(IRangeSource source, ZipLimits limits, IReadOnlyList<ZipEntryInfo> entries, bool isZip64)
    {
        _source = source;
        _limits = limits;
        Entries = entries;
        IsZip64 = isZip64;
    }

    public IReadOnlyList<ZipEntryInfo> Entries { get; }

    public bool IsZip64 { get; }

    public long ArchiveSize => _source.Length;

    public static async Task<ZipDirectory> ReadAsync(IRangeSource source, ZipLimits? limits = null, CancellationToken ct = default)
    {
        limits ??= new ZipLimits();
        if (source.Length < EocdMinSize)
            throw new InvalidDataException(L.T("ZipDirectory.29", $"Файл слишком короткий для ZIP."));

        var tailSize = (int)Math.Min(source.Length, EocdMinSize + MaxCommentSize + 20);
        var tailOffset = source.Length - tailSize;
        var tail = await source.ReadAsync(tailOffset, tailSize, ct).ConfigureAwait(false);

        var eocd = FindEocd(tail) ?? throw new InvalidDataException(L.T("ZipDirectory.28", $"Не найдено оглавление ZIP (End of Central Directory)."));
        var span = tail.AsSpan(eocd);
        long entries = BinaryPrimitives.ReadUInt16LittleEndian(span[10..]);
        long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);

        var isZip64 = false;
        if (eocd >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd - 20)) == Zip64LocatorSignature)
        {
            var zip64EocdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(eocd - 20 + 8));
            var rec = await source.ReadAsync(zip64EocdOffset, 56, ct).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(rec) != Zip64EocdSignature)
                throw new InvalidDataException(L.T("ZipDirectory.27", $"Повреждена запись ZIP64 End of Central Directory."));
            entries = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(32));
            cdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(40));
            cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(48));
            isZip64 = true;
        }

        if (entries > limits.MaxEntries)
            throw new InvalidDataException(L.T("ZipDirectory.26", $"Слишком много записей в архиве: {entries} (лимит {limits.MaxEntries})."));
        if (cdSize > limits.MaxCentralDirectorySize || cdOffset < 0 || cdOffset + cdSize > source.Length)
            throw new InvalidDataException(L.T("ZipDirectory.25", $"Некорректный размер или положение оглавления ZIP."));

        // Оглавление часто уже лежит в прочитанном хвосте.
        byte[] cd = cdOffset >= tailOffset
            ? tail.AsSpan((int)(cdOffset - tailOffset), (int)cdSize).ToArray()
            : await source.ReadAsync(cdOffset, (int)cdSize, ct).ConfigureAwait(false);

        return new ZipDirectory(source, limits, ParseCentralDirectory(cd, entries), isZip64);
    }

    /// <summary>Распакованное содержимое записи. Записи больше лимита не читаются.</summary>
    public async Task<byte[]> ReadEntryAsync(ZipEntryInfo entry, CancellationToken ct = default)
    {
        if (entry.UncompressedSize > _limits.MaxEntryReadSize || entry.CompressedSize > _limits.MaxEntryReadSize)
            throw new InvalidDataException(L.T("ZipDirectory.24", $"Запись {entry.Name} слишком большая ({entry.UncompressedSize} байт)."));

        var local = await _source.ReadAsync(entry.LocalHeaderOffset, 30, ct).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(local) != LocalHeaderSignature)
            throw new InvalidDataException(L.T("ZipDirectory.23", $"Повреждён локальный заголовок записи {entry.Name}."));
        var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(26));
        var extraLen = BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(28));
        var dataOffset = entry.LocalHeaderOffset + 30 + nameLen + extraLen;

        var compressed = await _source.ReadAsync(dataOffset, (int)entry.CompressedSize, ct).ConfigureAwait(false);
        switch (entry.Method)
        {
            case 0:
                return compressed;
            case 8:
            {
                using var input = new MemoryStream(compressed);
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = await deflate.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    output.Write(buffer, 0, read);
                    if (output.Length > _limits.MaxEntryReadSize)
                        throw new InvalidDataException(L.T("ZipDirectory.22", $"Запись {entry.Name} при распаковке превышает лимит."));
                }

                return output.ToArray();
            }

            default:
                throw new NotSupportedException(L.T("ZipDirectory.21", $"Метод сжатия {entry.Method} в записи {entry.Name} не поддерживается."));
        }
    }

    public ZipEntryInfo? Find(string name) =>
        Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? Entries.FirstOrDefault(e => string.Equals(e.Name.Replace('\\', '/'), name.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

    private static int? FindEocd(byte[] tail)
    {
        for (var i = tail.Length - EocdMinSize; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != EocdSignature)
                continue;
            var commentLen = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20));
            if (i + EocdMinSize + commentLen == tail.Length)
                return i;
        }

        return null;
    }

    private static List<ZipEntryInfo> ParseCentralDirectory(byte[] cd, long expected)
    {
        var list = new List<ZipEntryInfo>((int)Math.Min(expected, 100_000));
        var pos = 0;
        while (pos + 46 <= cd.Length && list.Count < expected)
        {
            var s = cd.AsSpan(pos);
            if (BinaryPrimitives.ReadUInt32LittleEndian(s) != CentralHeaderSignature)
                throw new InvalidDataException(L.T("ZipDirectory.20", $"Повреждено оглавление ZIP."));

            var method = BinaryPrimitives.ReadUInt16LittleEndian(s[10..]);
            long csize = BinaryPrimitives.ReadUInt32LittleEndian(s[20..]);
            long usize = BinaryPrimitives.ReadUInt32LittleEndian(s[24..]);
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(s[28..]);
            var extraLen = BinaryPrimitives.ReadUInt16LittleEndian(s[30..]);
            var commentLen = BinaryPrimitives.ReadUInt16LittleEndian(s[32..]);
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(s[42..]);
            if (pos + 46 + nameLen + extraLen + commentLen > cd.Length)
                throw new InvalidDataException(L.T("ZipDirectory.19", $"Оглавление ZIP обрезано."));

            // Бит 11 флагов — имя в UTF-8, иначе CP437. Имена в .yak — ASCII, где обе кодировки совпадают,
            // поэтому декодируется как UTF-8 без подключения кодовых страниц.
            var name = Encoding.UTF8.GetString(s.Slice(46, nameLen));

            // ZIP64 extra (0x0001): поля, равные 0xFFFFFFFF, лежат здесь в порядке usize, csize, offset.
            var extra = s.Slice(46 + nameLen, extraLen);
            var e = 0;
            while (e + 4 <= extra.Length)
            {
                var id = BinaryPrimitives.ReadUInt16LittleEndian(extra[e..]);
                var size = BinaryPrimitives.ReadUInt16LittleEndian(extra[(e + 2)..]);
                if (id == 0x0001)
                {
                    var f = extra.Slice(e + 4, Math.Min(size, extra.Length - e - 4));
                    var k = 0;
                    if (usize == 0xFFFFFFFF && k + 8 <= f.Length) { usize = (long)BinaryPrimitives.ReadUInt64LittleEndian(f[k..]); k += 8; }
                    if (csize == 0xFFFFFFFF && k + 8 <= f.Length) { csize = (long)BinaryPrimitives.ReadUInt64LittleEndian(f[k..]); k += 8; }
                    if (offset == 0xFFFFFFFF && k + 8 <= f.Length) { offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(f[k..]); }
                }

                e += 4 + size;
            }

            list.Add(new ZipEntryInfo(name, csize, usize, offset, method, name.EndsWith('/') || name.EndsWith('\\')));
            pos += 46 + nameLen + extraLen + commentLen;
        }

        return list;
    }
}
