using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using YakUI.Core.Logging;
using YakUI.Core.Storage;
using YakUI.Core.Localization;

namespace YakUI.Core.Archive;

/// <summary>
/// Инспекция дистрибутивов каталога по URL (FR-31, FR-36b) с кэшем в SQLite: опубликованный <c>.yak</c> неизменен,
/// поэтому результат хранится бессрочно по URL. Только HTTPS (NFR-04).
/// </summary>
public sealed class YakInspectionService : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly YakUiDatabase _db;
    private readonly HttpClient _http;
    private readonly IAppLog? _log;
    private readonly TimeProvider _time;

    public YakInspectionService(YakUiDatabase db, HttpMessageHandler? handler = null, IAppLog? log = null, TimeProvider? time = null)
    {
        _db = db;
        _log = log;
        _time = time ?? TimeProvider.System;
        _http = handler is null
            ? new HttpClient(Net.SwitchableProxy.CreateHandler())
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YakUI", AppInfo.Version));
    }

    /// <summary>Порог полной загрузки, если сервер не поддерживает Range (FR-34: 5 МБ).</summary>
    public long MaxFullDownload { get; init; } = 5 * 1024 * 1024;

    public YakInspection? GetCached(string url) => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM yak_inspections WHERE url = $u;";
        cmd.Parameters.AddWithValue("$u", url);
        // Результат старого формата (без .ghuser/.gh, FR-31) — промах кэша: архив перечитывается частично.
        return cmd.ExecuteScalar() is string s && JsonSerializer.Deserialize<YakInspection>(s, Json) is { } i && i.FormatVersion >= YakInspector.FormatVersion
            ? i
            : null;
    });

    public async Task<YakInspection> InspectAsync(string url, string? filename, CancellationToken ct = default)
    {
        if (GetCached(url) is { } cached)
            return cached;

        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(L.T("YakInspectionService.16", $"Инспекция только по HTTPS (NFR-04): {url}"));

        var source = await HttpRangeSource.OpenAsync(_http, uri, maxFullDownload: MaxFullDownload, ct: ct).ConfigureAwait(false);
        var (inspection, _) = await YakInspector.InspectAsync(source, filename, ct: ct).ConfigureAwait(false);
        _log?.Info(L.Ti("YakInspectionService.15", $"Инспекция {filename ?? url}: {inspection.Entries.Count} файлов, архив {inspection.ArchiveSize / 1024} КБ, прочитано {inspection.BytesRead / 1024} КБ{(inspection.ReadWholeFile ? L.T("YakInspectionService.14", $" (целиком, Range не поддерживается)") : "")}."));

        Store(url, inspection);
        return inspection;
    }

    internal void Store(string url, YakInspection inspection) => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO yak_inspections (url, filename, inspected_at, json) VALUES ($u, $f, $t, $j);";
        cmd.Parameters.AddWithValue("$u", url);
        cmd.Parameters.AddWithValue("$f", (object?)inspection.Filename ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(inspection, Json));
        cmd.ExecuteNonQuery();
    });

    public void Dispose() => _http.Dispose();
}
