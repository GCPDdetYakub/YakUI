using System.Globalization;
using System.IO.Compression;
using System.IO.Hashing;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using YakUI.Core.Logging;
using YakUI.Core.Localization;

namespace YakUI.Core.Downloads;

/// <summary>Что скачать: прямая ссылка из API (FR-36) и данные для реестра.</summary>
public sealed record DownloadRequest(string Url, string Filename, string? Package = null, string? Version = null, string? Tag = null, long? ExpectedSize = null, DateTimeOffset? PublishedAt = null);

public sealed record DownloadProgress(long Done, long? Total, bool Verifying = false);

public sealed class DownloadFailedException : Exception
{
    public DownloadFailedException(string message, Exception? inner = null, bool isGone = false) : base(message, inner) => IsGone = isGone;

    /// <summary>Сервер ответил 404/410: файла больше нет (FR-37a), повтор бесполезен.</summary>
    public bool IsGone { get; }
}

/// <summary>
/// Загрузка <c>.yak</c> в библиотеку (FR-35…FR-37). Пишет в <c>&lt;файл&gt;.part</c> и докачивает его по Range,
/// если сервер подтверждает, что файл тот же (<c>If-Range</c> с ETag или Last-Modified). После загрузки — проверка:
/// размер, оглавление ZIP и CRC32 каждой записи, затем SHA-256; только после этого <c>.part</c> переименовывается
/// в итоговый файл (NFR-05) и запись попадает в реестр. Уже скачанное (тот же URL, файл на месте, размер совпадает)
/// повторно не загружается. Только HTTPS (NFR-04).
/// </summary>
public sealed class Downloader : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly DownloadRegistry _registry;
    private readonly IAppLog? _log;
    private readonly OperationJournal? _journal;
    private readonly TimeProvider _time;

    public Downloader(string directory, DownloadRegistry registry, HttpMessageHandler? handler = null, IAppLog? log = null, OperationJournal? journal = null, TimeProvider? time = null)
    {
        Directory = directory;
        _registry = registry;
        _log = log;
        _journal = journal;
        _time = time ?? TimeProvider.System;
        _http = handler is null
            ? new HttpClient(Net.SwitchableProxy.CreateHandler())
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan; // большие файлы; обрыв ловится через таймаут чтения
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YakUI", AppInfo.Version));
    }

    /// <summary>Папка загрузок (FR-35).</summary>
    /// <summary>Папка загрузок (FR-35); меняется из «Настроек», новые загрузки идут в новую папку.</summary>
    public string Directory { get; set; }

    /// <summary>Таймаут ожидания данных от сервера при чтении.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Уже скачанный файл для URL, если он на месте; иначе null.</summary>
    public DownloadRecord? FindExisting(string url) =>
        _registry.FindByUrl(url) is { } r && File.Exists(r.Path) && new FileInfo(r.Path).Length == r.Size ? r : null;

    public async Task<DownloadRecord> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new DownloadFailedException(L.T("Downloader.46", $"Скачивание только по HTTPS: {request.Url}"));

        if (FindExisting(request.Url) is { } existing)
        {
            progress?.Report(new DownloadProgress(existing.Size, existing.Size));
            return existing;
        }

        var filename = SafeFileName(request.Filename);
        System.IO.Directory.CreateDirectory(Directory);
        var target = Path.Combine(Directory, filename);
        var part = target + ".part";
        var partMeta = part + ".json";

        try
        {
            var lastModified = await FetchAsync(request, uri, part, partMeta, progress, ct).ConfigureAwait(false);

            progress?.Report(new DownloadProgress(new FileInfo(part).Length, new FileInfo(part).Length, Verifying: true));
            var size = new FileInfo(part).Length;
            if (request.ExpectedSize is { } expected && expected != size)
                throw new DownloadFailedException(L.Ti("Downloader.45", $"Размер {size} не совпадает с ожидаемым {expected}."));

            await Task.Run(() => VerifyZip(part, ct), ct).ConfigureAwait(false);
            var sha = await Sha256Async(part, ct).ConfigureAwait(false);

            File.Move(part, target, overwrite: true);
            File.Delete(partMeta);

            // FR-37b: дата файла — как на сервере (curl -R); без Last-Modified дата не трогается.
            if (lastModified is { } lm)
                File.SetLastWriteTimeUtc(target, lm.UtcDateTime);

            var record = _registry.Upsert(new DownloadRecord(0, request.Url, filename, target, size, sha, request.Package, request.Version, request.Tag, _time.GetUtcNow(), lastModified, request.PublishedAt));
            _log?.Info($"Скачан {filename}: {size / 1024} КБ, SHA-256 {sha}.");
            _journal?.Write("download", filename, "ok", details: new { request.Url, size, sha });
            return record;
        }
        catch (InvalidDataException e)
        {
            // Повреждённый архив: незавершённый файл больше не годится для докачки.
            DeleteQuietly(part);
            DeleteQuietly(partMeta);
            _journal?.Write("download", filename, "invalid", details: new { request.Url, error = e.Message });
            throw new DownloadFailedException(L.T("Downloader.44", $"Архив повреждён: {e.Message}"), e);
        }
        catch (DownloadFailedException e)
        {
            DeleteQuietly(part);
            DeleteQuietly(partMeta);
            _journal?.Write("download", filename, e.IsGone ? "gone" : "error", details: new { request.Url, error = e.Message });
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException)
        {
            // .part остаётся — следующая попытка докачает.
            _journal?.Write("download", filename, "error", details: new { request.Url, error = e.Message });
            throw new DownloadFailedException(e.Message, e);
        }
    }

    /// <returns>Last-Modified файла на сервере (из ответа или сохранённый при первом запросе).</returns>
    private async Task<DateTimeOffset?> FetchAsync(DownloadRequest request, Uri uri, string part, string partMeta, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var meta = ReadPartMeta(partMeta);
        var resumeFrom = meta is not null && meta.Url == request.Url && File.Exists(part) ? new FileInfo(part).Length : 0;

        using var http = new HttpRequestMessage(HttpMethod.Get, uri);
        if (resumeFrom > 0)
        {
            http.Headers.Range = new RangeHeaderValue(resumeFrom, null);
            if (meta!.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
                http.Headers.IfRange = new RangeConditionHeaderValue(tag);
            else if (meta.LastModified is { } lm)
                http.Headers.IfRange = new RangeConditionHeaderValue(lm);
        }

        using var response = await _http.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && resumeFrom > 0)
            return meta!.LastModified; // .part уже полный — сразу к проверке
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            throw new DownloadFailedException(L.T("Downloader.43", $"Файл удалён с сервера (HTTP {(int)response.StatusCode})."), isGone: true);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);

        var append = response.StatusCode == HttpStatusCode.PartialContent && resumeFrom > 0;
        if (!append)
            resumeFrom = 0; // сервер прислал файл целиком (изменился или без Range) — начинаем заново

        var total = append
            ? response.Content.Headers.ContentRange?.Length
            : response.Content.Headers.ContentLength;
        var lastModified = response.Content.Headers.LastModified ?? (append ? meta!.LastModified : null);
        WritePartMeta(partMeta, new PartMeta(request.Url, response.Headers.ETag?.ToString() ?? (append ? meta!.ETag : null), lastModified));

        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buffer = new byte[1 << 17];
        var done = resumeFrom;
        var lastReport = DateTime.MinValue;
        while (true)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(ReadTimeout);
            int read;
            try
            {
                read = await input.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(L.T("Downloader.42", $"Сервер перестал отвечать во время загрузки."));
            }

            if (read == 0)
                break;
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 200)
            {
                progress?.Report(new DownloadProgress(done, total));
                lastReport = DateTime.UtcNow;
            }
        }

        progress?.Report(new DownloadProgress(done, total));
        if (total is { } t && done != t)
            throw new IOException(L.Ti("Downloader.41", $"Загрузка прервалась: получено {done} из {t} байт."));
        return lastModified;
    }

    /// <summary>Оглавление ZIP читается и для каждой записи сверяется CRC32 распакованных данных (FR-37).</summary>
    internal static void VerifyZip(string path, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count == 0)
            throw new InvalidDataException(L.T("Downloader.40", $"в архиве нет файлов"));
        if (zip.GetEntry("manifest.yml") is null && !zip.Entries.Any(e => e.FullName.Equals("manifest.yml", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(L.T("Downloader.39", $"в архиве нет manifest.yml"));

        var buffer = new byte[1 << 16];
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/'))
                continue;
            var crc = new Crc32();
            using var s = entry.Open();
            int read;
            while ((read = s.Read(buffer)) > 0)
                crc.Append(buffer.AsSpan(0, read));
            if (crc.GetCurrentHashAsUInt32() != entry.Crc32)
                throw new InvalidDataException(L.T("Downloader.38", $"не сходится CRC32 у {entry.FullName}"));
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(s, ct).ConfigureAwait(false)).ToLowerInvariant();
    }

    /// <summary>Имя файла из API как имя в папке загрузок: без путей и недопустимых символов.</summary>
    internal static string SafeFileName(string filename)
    {
        // Последний сегмент пути, без Path.GetFileName: он считает «a:» диском и отрезал бы часть имени.
        var name = filename.Replace('\\', '/').Split('/').Last();
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (name.Length == 0 || name is "." or "..")
            throw new DownloadFailedException(L.T("Downloader.37", $"Недопустимое имя файла: {filename}"));
        return name;
    }

    private static PartMeta? ReadPartMeta(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<PartMeta>(File.ReadAllText(path), Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WritePartMeta(string path, PartMeta meta) => File.WriteAllText(path, JsonSerializer.Serialize(meta, Json));

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed record PartMeta(string Url, string? ETag, DateTimeOffset? LastModified);
}
