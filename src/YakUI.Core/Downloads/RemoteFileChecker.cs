using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using YakUI.Core.Logging;

namespace YakUI.Core.Downloads;

public enum RemoteFileState
{
    /// <summary>Сервер подтвердил наличие (2xx).</summary>
    Exists,

    /// <summary>404 или 410 — файла на сервере больше нет (FR-37a).</summary>
    Gone,

    /// <summary>Нет сети, таймаут или другой ответ — удалённым файл не считается.</summary>
    Unknown,
}

/// <summary>
/// Проверка существования файла дистрибутива на сервере (FR-37a): один <c>HEAD</c> по запросу карточки, без обхода
/// каталога. Определённый ответ (есть / удалён) запоминается до конца работы приложения; «неизвестно» — нет,
/// чтобы следующее открытие карточки проверило снова. Удаление пишется в журнал.
/// </summary>
public sealed class RemoteFileChecker : IDisposable
{
    private readonly HttpClient _http;
    private readonly OperationJournal? _journal;
    private readonly IAppLog? _log;
    private readonly ConcurrentDictionary<string, RemoteFileState> _known = new(StringComparer.Ordinal);

    public RemoteFileChecker(HttpMessageHandler? handler = null, OperationJournal? journal = null, IAppLog? log = null)
    {
        _journal = journal;
        _log = log;
        _http = handler is null
            ? new HttpClient(Net.SwitchableProxy.CreateHandler())
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YakUI", AppInfo.Version));
    }

    /// <summary>Уже известное состояние без сети; null — ещё не проверялось.</summary>
    public RemoteFileState? Known(string url) => _known.TryGetValue(url, out var s) ? s : null;

    /// <summary>
    /// Запомнить результат, полученный иначе: свежий Range-запрос разбора архива уже подтвердил наличие (или 404/410).
    /// </summary>
    public void Record(string url, RemoteFileState state, string? filename = null)
    {
        if (state == RemoteFileState.Unknown)
            return;
        if (_known.TryAdd(url, state) && state == RemoteFileState.Gone)
            ReportGone(url, filename, "разбор архива");
    }

    public async Task<RemoteFileState> CheckAsync(string url, string? filename = null, CancellationToken ct = default)
    {
        if (Known(url) is { } known)
            return known;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return RemoteFileState.Unknown;

        RemoteFileState state;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            state = response.StatusCode switch
            {
                HttpStatusCode.NotFound or HttpStatusCode.Gone => RemoteFileState.Gone,
                _ when response.IsSuccessStatusCode => RemoteFileState.Exists,
                _ => RemoteFileState.Unknown,
            };
        }
        catch (Exception e) when ((e is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            _log?.Debug($"HEAD {url}: {e.Message}");
            return RemoteFileState.Unknown;
        }

        if (state != RemoteFileState.Unknown && _known.TryAdd(url, state) && state == RemoteFileState.Gone)
            ReportGone(url, filename, "HEAD");
        return state;
    }

    private void ReportGone(string url, string? filename, string how)
    {
        _log?.Warning($"Файл удалён с сервера: {url} ({how}).");
        _journal?.Write("server-file", filename ?? url, "gone", details: new { url, how });
    }

    public void Dispose() => _http.Dispose();
}
