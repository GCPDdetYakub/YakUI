using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using YakUI.Core.Archive;
using YakUI.Core.Logging;
using YakUI.Core.Platform;

namespace YakUI.Core.Icons;

/// <summary>Откуда взята иконка (FR-34). Новые значения добавляются в конец — они хранятся числом в кэше.</summary>
public enum IconSource
{
    IconUrl,
    DownloadedYak,
    PartialRead,
    Placeholder,

    /// <summary><c>icon_url</c> из <c>manifest.yml</c> внутри архива (в API его нет).</summary>
    ManifestIconUrl,

    /// <summary><c>icon_url</c> более старой версии пакета: у последней версии иконки нет.</summary>
    OlderVersionIconUrl,

    /// <summary>Файл иконки в папке установленной версии.</summary>
    Installed,
}

/// <summary>Иконка установленного пакета: папка версии, поля icon/icon_url локального манифеста, запрос каталога.</summary>
public sealed record InstalledIconRequest(string Name, string Version, string? VersionDirectory, string? ManifestIcon, string? ManifestIconUrl, IconRequest? Catalog);

/// <summary>
/// Что нужно для поиска иконки пакета: последняя версия, её <c>icon_url</c>, один файл <c>.yak</c>
/// и <c>icon_url</c> самой новой версии, у которой он есть (запасной источник).
/// </summary>
public sealed record IconRequest(string Name, string Version, string? IconUrl, string? YakUrl, string? YakFilename, string? FallbackIconUrl = null);

/// <summary>
/// Результат: путь к проверенной картинке в кэше (или null — заглушка) и тип содержимого пакета,
/// если стал известен из оглавления архива (FR-34a: значок типа на заглушке).
/// </summary>
public sealed record IconResult(string? FilePath, IconSource Source, PackageContentKind? ContentKind);

/// <summary>
/// Иконки пакетов каталога (FR-34…FR-34c). Порядок:
/// (1) <c>icon_url</c> последней версии из API;
/// (2) <c>.yak</c>, уже лежащий в папке загрузок, — без сети;
/// (3) частичное чтение <c>.yak</c> (FR-36b): файл из поля <c>icon</c> манифеста, иначе картинка в корне архива
///     (<c>icon.*</c> → имя с «icon»/«logo» → единственная картинка в корне), иначе <c>icon_url</c> из манифеста;
///     без Range — полная загрузка только архива ≤ 5 МБ;
/// (4) <c>icon_url</c> более старой версии;
/// (5) заглушка.
/// Каждая картинка проверяется (FR-34c: PNG/JPEG, ≤ 1 МБ, ≤ 4096 px); больше 256 px — уменьшается до 256 px и
/// кладётся в кэш уменьшенной. Кэш <c>icons\&lt;имя&gt;\&lt;версия&gt;.&lt;ext&gt;</c> бессрочный; «иконки нет»
/// перепроверяется через 7 дней.
/// </summary>
public sealed class IconService : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RetryPlaceholderAfter = TimeSpan.FromDays(7);
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico"];

    private readonly string _cacheRoot;
    private volatile string? _downloadsDirectory;

    /// <summary>Папка загрузок для поиска уже скачанных .yak (FR-34d); меняется вместе с FR-35.</summary>
    public string? DownloadsDirectory
    {
        get => _downloadsDirectory;
        set => _downloadsDirectory = value;
    }
    private readonly HttpClient _http;
    private readonly IImageScaler? _scaler;
    private readonly IAppLog? _log;
    private readonly OperationJournal? _journal;
    private readonly TimeProvider _time;

    public IconService(
        string cacheRoot,
        string? downloadsDirectory = null,
        HttpMessageHandler? handler = null,
        IAppLog? log = null,
        OperationJournal? journal = null,
        TimeProvider? time = null,
        IImageScaler? scaler = null)
    {
        _cacheRoot = cacheRoot;
        _downloadsDirectory = downloadsDirectory;
        _log = log;
        _journal = journal;
        _time = time ?? TimeProvider.System;
        _scaler = scaler;
        _http = handler is null
            ? new HttpClient(Net.SwitchableProxy.CreateHandler(DecompressionMethods.All))
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YakUI", AppInfo.Version));
    }

    /// <summary>Порог полной загрузки .yak без Range (FR-34, настраивается).</summary>
    public long MaxFullDownload { get; init; } = 5 * 1024 * 1024;

    /// <summary>Игнорировать закэшированные заглушки (перепроверка после изменения правил).</summary>
    public bool RecheckPlaceholders { get; init; }

    /// <summary>Сколько байтов сети израсходовано на иконки за время работы (для отчёта).</summary>
    public long BytesDownloaded => Interlocked.Read(ref _bytes);

    private long _bytes;

    /// <summary>Из кэша без сети; null — в кэше ничего нет (или пора перепроверить заглушку).</summary>
    public IconResult? GetCached(IconRequest request)
    {
        var meta = ReadMeta(request);
        if (meta is null)
            return null;
        if (meta.File is null)
        {
            return !RecheckPlaceholders && _time.GetUtcNow() - meta.CheckedAt < RetryPlaceholderAfter
                ? new IconResult(null, IconSource.Placeholder, meta.Kind)
                : null;
        }

        var path = Path.Combine(PackageDir(request.Name), meta.File);
        if (!File.Exists(path))
            return null;

        // Перепроверка: иконки, закэшированные до FR-34c v0.2.11, уменьшаются до 256 px на месте.
        if (RecheckPlaceholders && _scaler is not null && ShrinkCachedIfLarge(path) is { } shrunk && shrunk != path)
        {
            Save(request, shrunk, meta.Source, meta.Kind);
            path = shrunk;
        }

        return new IconResult(path, meta.Source, meta.Kind);
    }

    private string? ShrinkCachedIfLarge(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var check = IconValidator.Check(bytes);
        if (!check.IsValid || Math.Max(check.Width, check.Height) <= IconValidator.CacheSide)
            return path;

        try
        {
            var png = _scaler!.DownscaleToPng(bytes, IconValidator.CacheSide);
            var target = Path.ChangeExtension(path, ".png");
            File.WriteAllBytes(target + ".tmp", png);
            File.Move(target + ".tmp", target, overwrite: true);
            if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
                File.Delete(path); // наш же файл кэша (.jpg), заменённый уменьшенным .png
            return target;
        }
        catch (InvalidDataException)
        {
            return path;
        }
    }

    public async Task<IconResult> GetAsync(IconRequest request, CancellationToken ct = default)
    {
        if (GetCached(request) is { } cached)
            return cached;

        PackageContentKind? kind = null;

        // (1) icon_url последней версии из API.
        if (await TryUrlAsync(request, request.IconUrl, "icon_url", ct).ConfigureAwait(false) is { } fromUrl)
            return Save(request, fromUrl, IconSource.IconUrl, kind);

        // (2) .yak уже в папке загрузок — без сети.
        if (_downloadsDirectory is not null && request.YakFilename is { } fn && SafeFileName(fn) is { } safe
            && File.Exists(Path.Combine(_downloadsDirectory, safe)))
        {
            try
            {
                var found = await TryFromArchiveAsync(new FileRangeSource(Path.Combine(_downloadsDirectory, safe)), request, ct).ConfigureAwait(false);
                kind ??= found.Kind;
                if (await FromArchiveFoundAsync(request, found, IconSource.DownloadedYak, "архив в загрузках", ct).ConfigureAwait(false) is { } fromLocal)
                    return fromLocal;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
            {
                _log?.Debug($"Иконка {request.Name} {request.Version}: архив в загрузках не прочитан ({e.Message}).");
            }
        }

        // (3) частичное чтение .yak последней версии (FR-36b); без Range — полная загрузка ≤ 5 МБ.
        if (request.YakUrl is { } yakUrl && IsHttps(yakUrl))
        {
            try
            {
                var source = await HttpRangeSource.OpenAsync(_http, new Uri(yakUrl), maxFullDownload: MaxFullDownload, ct: ct).ConfigureAwait(false);
                var found = await TryFromArchiveAsync(source, request, ct).ConfigureAwait(false);
                Interlocked.Add(ref _bytes, source.BytesTransferred);
                kind ??= found.Kind;
                if (await FromArchiveFoundAsync(request, found, IconSource.PartialRead, "архив .yak", ct).ConfigureAwait(false) is { } fromArchive)
                    return fromArchive;
            }
            catch (Exception e) when ((e is HttpRequestException or InvalidDataException or NotSupportedException or RangeNotSupportedException or TaskCanceledException) && !ct.IsCancellationRequested)
            {
                _log?.Debug($"Иконка {request.Name} {request.Version}: архив недоступен ({e.Message}).");
            }
        }

        // (4) icon_url более старой версии.
        if (request.FallbackIconUrl != request.IconUrl
            && await TryUrlAsync(request, request.FallbackIconUrl, "icon_url старой версии", ct).ConfigureAwait(false) is { } older)
            return Save(request, older, IconSource.OlderVersionIconUrl, kind);

        // (5) заглушка — запоминаем, чтобы не повторять поиск при каждом показе.
        return Save(request, null, IconSource.Placeholder, kind);
    }

    /// <summary>
    /// Иконка установленного пакета (FR-34): (1) файл из поля <c>icon</c> локального <c>manifest.yml</c>, иначе картинка
    /// в корне папки версии; (2) <c>icon_url</c> из локального манифеста; (3) иконка каталога; (4) заглушка.
    /// Результат кладётся в тот же кэш (имя/версия), проверка и уменьшение — как для каталога (FR-34c).
    /// </summary>
    public async Task<IconResult> GetInstalledAsync(InstalledIconRequest request, CancellationToken ct = default)
    {
        var key = new IconRequest(request.Name, request.Version, null, null, null);
        if (GetCached(key) is { FilePath: not null } cached)
            return cached;

        if (request.VersionDirectory is { } dir && Directory.Exists(dir))
        {
            var file = LocalIconFile(dir, request.ManifestIcon);
            if (file is not null && new FileInfo(file).Length <= IconValidator.MaxBytes
                && Accept(key, await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false), $"папка версии: {Path.GetFileName(file)}") is { } path)
                return Save(key, path, IconSource.Installed, null);
        }

        if (await TryUrlAsync(key, request.ManifestIconUrl, "icon_url локального манифеста", ct).ConfigureAwait(false) is { } fromUrl)
            return Save(key, fromUrl, IconSource.ManifestIconUrl, null);

        if (request.Catalog is { } catalog)
            return await GetAsync(catalog, ct).ConfigureAwait(false);

        return Save(key, null, IconSource.Placeholder, null);
    }

    /// <summary>Файл иконки в папке версии: из поля icon (только внутри папки), иначе картинка в её корне.</summary>
    internal static string? LocalIconFile(string versionDirectory, string? manifestIcon)
    {
        var root = Path.GetFullPath(versionDirectory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (manifestIcon is { } named && !YakInspector.IsUnsafe(named))
        {
            var candidate = Path.GetFullPath(Path.Combine(root, named));
            if (candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                return candidate;
        }

        var images = Directory.EnumerateFiles(root)
            .Where(f => ImageExtensions.Any(x => f.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return images.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals("icon", StringComparison.OrdinalIgnoreCase))
            ?? images.Where(f => Path.GetFileName(f).Contains("icon", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Contains("logo", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            ?? (images.Count == 1 ? images[0] : null);
    }

    /// <summary>FR-34d, внутри архива: поле icon → icon_url манифеста → картинка из корня.</summary>
    private async Task<IconResult?> FromArchiveFoundAsync(IconRequest request, ArchiveIcon found, IconSource source, string from, CancellationToken ct)
    {
        if (found.Icon is { } bytes && Accept(request, bytes, $"{from}: {found.IconPath}") is { } path)
            return Save(request, path, source, found.Kind);
        if (await TryUrlAsync(request, found.ManifestIconUrl, "icon_url манифеста", ct).ConfigureAwait(false) is { } fromManifest)
            return Save(request, fromManifest, IconSource.ManifestIconUrl, found.Kind);
        if (found.RootIcon is { } rootBytes && Accept(request, rootBytes, $"{from}: {found.RootPath} (корень)") is { } rootPath)
            return Save(request, rootPath, source, found.Kind);
        return null;
    }

    private async Task<string?> TryUrlAsync(IconRequest request, string? url, string from, CancellationToken ct)
    {
        if (url is null || !IsHttps(url))
            return null;
        var bytes = await TryDownloadIconAsync(url, ct).ConfigureAwait(false);
        return bytes is null ? null : Accept(request, bytes, from);
    }

    private async Task<byte[]?> TryDownloadIconAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            if (response.Content.Headers.ContentLength > IconValidator.MaxBytes)
                return new byte[IconValidator.MaxBytes + 1]; // отбракуется по размеру без загрузки

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                ms.Write(buffer, 0, read);
                if (ms.Length > IconValidator.MaxBytes)
                    break; // хватит, чтобы проверка отбраковала по размеру
            }

            Interlocked.Add(ref _bytes, ms.Length);
            return ms.ToArray();
        }
        catch (Exception e) when ((e is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            _log?.Debug($"icon_url {url} недоступен: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Иконка из архива. <see cref="Icon"/> — из поля icon; <see cref="RootIcon"/> — картинка из корня
    /// (используется после <c>icon_url</c> манифеста, FR-34d).
    /// </summary>
    private sealed record ArchiveIcon(byte[]? Icon, string? IconPath, string? ManifestIconUrl, byte[]? RootIcon, string? RootPath, PackageContentKind? Kind);

    /// <summary>
    /// Иконка из архива: файл из поля <c>icon</c> манифеста; если его нет — картинка в корне архива.
    /// Заодно — тип содержимого и <c>icon_url</c> манифеста.
    /// </summary>
    private static async Task<ArchiveIcon> TryFromArchiveAsync(IRangeSource source, IconRequest request, CancellationToken ct)
    {
        var (inspection, dir) = await YakInspector.InspectAsync(source, request.YakFilename, new ZipLimits { MaxEntryReadSize = IconValidator.MaxBytes + 1 }, ct).ConfigureAwait(false);
        var manifestUrl = inspection.Manifest?.IconUrl;

        var named = inspection.Manifest?.Icon is { } n && !YakInspector.IsUnsafe(n) ? dir.Find(n) : null;
        var root = named is null ? FindRootImage(dir) : null;

        return new ArchiveIcon(
            await ReadAsync(named).ConfigureAwait(false), named?.Name,
            manifestUrl,
            await ReadAsync(root).ConfigureAwait(false), root?.Name,
            inspection.ContentKind);

        async Task<byte[]?> ReadAsync(ZipEntryInfo? entry) => entry switch
        {
            null => null,
            { UncompressedSize: > IconValidator.MaxBytes } => new byte[IconValidator.MaxBytes + 1], // отбракуется по размеру
            _ => await dir.ReadEntryAsync(entry, ct).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Картинка PNG/JPEG в корне архива, когда манифест иконку не называет: <c>icon.*</c>, затем имя с «icon» или «logo»,
    /// затем единственная картинка в корне. Вложенные папки не просматриваются (там бывают чужие картинки библиотек).
    /// </summary>
    internal static ZipEntryInfo? FindRootImage(ZipDirectory dir)
    {
        var root = dir.Entries
            .Where(e => !e.IsDirectory && !e.Name.Replace('\\', '/').Contains('/') && ImageExtensions.Any(x => e.Name.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return root.FirstOrDefault(e => Path.GetFileNameWithoutExtension(e.Name).Equals("icon", StringComparison.OrdinalIgnoreCase))
            ?? root.Where(e => e.Name.Contains("icon", StringComparison.OrdinalIgnoreCase) || e.Name.Contains("logo", StringComparison.OrdinalIgnoreCase))
                   .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            ?? (root.Count == 1 ? root[0] : null);
    }

    /// <summary>FR-34c: проверка; больше 256 px — уменьшение; при отказе — запись в журнал и null.</summary>
    private string? Accept(IconRequest request, byte[] bytes, string from)
    {
        var check = IconValidator.Check(bytes);
        if (!check.IsValid)
        {
            _log?.Warning($"Иконка {request.Name} {request.Version} ({from}) отклонена: {check.Problem}.");
            _journal?.Write("icon", $"{request.Name} {request.Version}", "rejected", details: new { from, check.Problem, size = bytes.Length });
            return null;
        }

        var extension = check.Extension;
        if (check.NeedsConversion && _scaler is null)
        {
            _log?.Warning($"Иконка {request.Name} {request.Version} ({from}) отклонена: {check.Format} без конвертера в PNG.");
            return null;
        }

        if ((check.NeedsConversion || Math.Max(check.Width, check.Height) > IconValidator.CacheSide) && _scaler is not null)
        {
            try
            {
                bytes = _scaler.DownscaleToPng(bytes, IconValidator.CacheSide);
                extension = "png";
            }
            catch (InvalidDataException e)
            {
                _log?.Warning($"Иконка {request.Name} {request.Version} ({from}) не уменьшена: {e.Message}.");
                _journal?.Write("icon", $"{request.Name} {request.Version}", "rejected", details: new { from, problem = e.Message });
                return null;
            }
        }

        var dir = PackageDir(request.Name);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{SafeSegment(request.Version)}.{extension}");
        var tmp = file + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, file, overwrite: true);
        return file;
    }

    private IconResult Save(IconRequest request, string? path, IconSource source, PackageContentKind? kind)
    {
        var dir = PackageDir(request.Name);
        Directory.CreateDirectory(dir);
        var meta = new IconMeta(path is null ? null : Path.GetFileName(path), source, kind, _time.GetUtcNow());
        var metaPath = MetaPath(request);
        var tmp = metaPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(meta, Json));
        File.Move(tmp, metaPath, overwrite: true);
        return new IconResult(path, source, kind);
    }

    private IconMeta? ReadMeta(IconRequest request)
    {
        var path = MetaPath(request);
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<IconMeta>(File.ReadAllText(path), Json);
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    private string PackageDir(string name) => Path.Combine(_cacheRoot, SafeSegment(name));

    private string MetaPath(IconRequest r) => Path.Combine(PackageDir(r.Name), SafeSegment(r.Version) + ".meta.json");

    /// <summary>Имя пакета или версии как сегмент пути: без недопустимых символов, без «..».</summary>
    internal static string SafeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimEnd('.');
        return s.Length == 0 || s is "." or ".." ? "_" : s;
    }

    private static string? SafeFileName(string filename)
    {
        var name = Path.GetFileName(filename);
        return name.Length == 0 || name != filename ? null : name;
    }

    private static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    public void Dispose() => _http.Dispose();

    private sealed record IconMeta(string? File, IconSource Source, PackageContentKind? Kind, DateTimeOffset CheckedAt);
}
