using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using YakUI.Core.Localization;

namespace YakUI.Core.Archive;

/// <summary>Источник байтов с произвольным доступом: HTTP Range, файл или память (FR-36b).</summary>
public interface IRangeSource
{
    long Length { get; }

    /// <summary>Сколько байтов фактически получено из источника (для журнала и отчёта).</summary>
    long BytesTransferred { get; }

    Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct = default);
}

public sealed class RangeNotSupportedException : Exception
{
    public RangeNotSupportedException(string message) : base(message) { }
}

public sealed class MemoryRangeSource : IRangeSource
{
    private readonly byte[] _data;

    public MemoryRangeSource(byte[] data) => _data = data;

    public long Length => _data.Length;

    public long BytesTransferred { get; private set; }

    public Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct = default)
    {
        if (offset < 0 || count < 0 || offset + count > _data.Length)
            throw new InvalidDataException(L.T("RangeSources.13", $"Чтение за пределами архива."));
        BytesTransferred += count;
        return Task.FromResult(_data.AsSpan((int)offset, count).ToArray());
    }
}

public sealed class FileRangeSource : IRangeSource
{
    private readonly string _path;

    public FileRangeSource(string path)
    {
        _path = path;
        Length = new FileInfo(path).Length;
    }

    public long Length { get; }

    public long BytesTransferred { get; private set; }

    public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct = default)
    {
        if (offset < 0 || count < 0 || offset + count > Length)
            throw new InvalidDataException(L.T("RangeSources.12", $"Чтение за пределами архива."));
        await using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        fs.Position = offset;
        var buffer = new byte[count];
        await fs.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        BytesTransferred += count;
        return buffer;
    }
}

/// <summary>
/// Частичное чтение файла по HTTP Range (FR-36b). Первый запрос — хвост (<c>bytes=-N</c>), из
/// <c>Content-Range</c> берётся полный размер; хвост кэшируется, дальнейшие чтения — точечными Range-запросами.
/// Сервер без Range (ответ 200) допускается, только если файл не больше <c>maxFullDownload</c> (правило FR-34),
/// иначе <see cref="RangeNotSupportedException"/>.
/// </summary>
public sealed class HttpRangeSource : IRangeSource
{
    private readonly HttpClient _http;
    private readonly Uri _uri;
    private byte[] _cached = [];
    private long _cachedOffset;

    private HttpRangeSource(HttpClient http, Uri uri)
    {
        _http = http;
        _uri = uri;
    }

    public long Length { get; private set; }

    public long BytesTransferred { get; private set; }

    /// <summary>Сервер отдал файл целиком (Range не поддерживается).</summary>
    public bool FullDownload { get; private set; }

    public static async Task<HttpRangeSource> OpenAsync(HttpClient http, Uri uri, int tailSize = 66 * 1024, long maxFullDownload = 5 * 1024 * 1024, CancellationToken ct = default)
    {
        var source = new HttpRangeSource(http, uri);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(null, tailSize);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange
                ?? throw new InvalidDataException(L.T("RangeSources.11", $"Ответ 206 без Content-Range."));
            source.Length = range.Length ?? throw new InvalidDataException(L.T("RangeSources.10", $"Content-Range без полного размера."));
            source._cachedOffset = range.From ?? source.Length - tailSize;
            source._cached = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Файл короче хвоста или пуст — читаем целиком.
            return await FullAsync(http, uri, maxFullDownload, ct).ConfigureAwait(false);
        }
        else if (response.IsSuccessStatusCode)
        {
            var len = response.Content.Headers.ContentLength;
            if (len is null || len > maxFullDownload)
                throw new RangeNotSupportedException(L.T("RangeSources.9", $"Сервер не поддерживает Range, а файл больше {maxFullDownload / 1024 / 1024} МБ."));
            source._cached = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            source._cachedOffset = 0;
            source.Length = source._cached.Length;
            source.FullDownload = true;
        }
        else
        {
            throw new HttpRequestException($"{uri}: HTTP {(int)response.StatusCode}", null, response.StatusCode);
        }

        source.BytesTransferred = source._cached.Length;
        return source;
    }

    private static async Task<HttpRangeSource> FullAsync(HttpClient http, Uri uri, long maxFullDownload, CancellationToken ct)
    {
        var source = new HttpRangeSource(http, uri);
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxFullDownload)
            throw new RangeNotSupportedException(L.T("RangeSources.8", $"Файл слишком большой для полной загрузки."));
        source._cached = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        source.Length = source._cached.Length;
        source.BytesTransferred = source._cached.Length;
        source.FullDownload = true;
        return source;
    }

    public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct = default)
    {
        if (offset < 0 || count < 0 || offset + count > Length)
            throw new InvalidDataException(L.T("RangeSources.7", $"Чтение за пределами архива."));
        if (count == 0)
            return [];

        if (offset >= _cachedOffset && offset + count <= _cachedOffset + _cached.Length)
            return _cached.AsSpan((int)(offset - _cachedOffset), count).ToArray();

        using var request = new HttpRequestMessage(HttpMethod.Get, _uri);
        request.Headers.Range = new RangeHeaderValue(offset, offset + count - 1);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new RangeNotSupportedException(L.Ti("RangeSources.6", $"Ожидался ответ 206 на Range {offset}-{offset + count - 1}, получен {(int)response.StatusCode}."));

        var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (data.Length != count)
            throw new InvalidDataException(L.Ti("RangeSources.5", $"Сервер вернул {data.Length} байт вместо {count}."));
        BytesTransferred += data.Length;
        return data;
    }
}
