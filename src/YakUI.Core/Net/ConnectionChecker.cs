using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using YakUI.Core.Logging;
using YakUI.Core.Localization;

namespace YakUI.Core.Net;

public sealed record ConnectionStep(string Target, bool Ok, string Message, TimeSpan Duration);

public sealed record ConnectionReport(ProxySettings Proxy, IReadOnlyList<ConnectionStep> Steps)
{
    public bool Ok => Steps.All(s => s.Ok);
}

/// <summary>
/// «Проверить соединение» (FR-95b): запрос к серверу Yak и чтение первых байт файла с files.mcneel.com через
/// проверяемый режим прокси (ещё не сохранённый — отдельный обработчик). Итог — в журнал.
/// </summary>
public sealed class ConnectionChecker
{
    public static readonly Uri DefaultApi = new("https://yak.rhino3d.com/packages");

    private readonly OperationJournal? _journal;
    private readonly Func<ProxySettings, HttpMessageHandler> _handlerFactory;

    public ConnectionChecker(OperationJournal? journal = null, Func<ProxySettings, HttpMessageHandler>? handlerFactory = null)
    {
        _journal = journal;
        _handlerFactory = handlerFactory ?? (s =>
        {
            var proxy = new SwitchableProxy { Settings = s };
            return new SocketsHttpHandler { Proxy = proxy, UseProxy = true, ConnectTimeout = TimeSpan.FromSeconds(15) };
        });
    }

    public async Task<ConnectionReport> CheckAsync(ProxySettings proxy, Uri? fileUrl, Uri? apiUrl = null, CancellationToken ct = default)
    {
        if (proxy.Validate() is { } invalid)
        {
            var bad = new ConnectionReport(proxy, [new ConnectionStep(L.T("ConnectionChecker.123", $"прокси"), false, invalid, TimeSpan.Zero)]);
            Journal(bad);
            return bad;
        }

        using var http = new HttpClient(_handlerFactory(proxy), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YakUI", AppInfo.Version));

        var steps = new List<ConnectionStep>
        {
            await StepAsync(http, apiUrl ?? DefaultApi, rangeBytes: null, ct).ConfigureAwait(false),
        };
        if (fileUrl is not null)
            steps.Add(await StepAsync(http, fileUrl, rangeBytes: 16, ct).ConfigureAwait(false));

        var report = new ConnectionReport(proxy, steps);
        Journal(report);
        return report;
    }

    private static async Task<ConnectionStep> StepAsync(HttpClient http, Uri url, int? rangeBytes, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (rangeBytes is { } n)
                request.Headers.Range = new RangeHeaderValue(0, n - 1);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                return new ConnectionStep(url.Host, false, string.Create(CultureInfo.InvariantCulture, $"HTTP {code}"), sw.Elapsed);

            var buffer = new byte[Math.Max(rangeBytes ?? 1, 1)];
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            return new ConnectionStep(url.Host, true,
                L.Ti("ConnectionChecker.122", $"HTTP {code}, получено {read} Б за {sw.ElapsedMilliseconds} мс"), sw.Elapsed);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or NotSupportedException)
        {
            if (ct.IsCancellationRequested)
                throw;
            var message = e is TaskCanceledException ? L.T("ConnectionChecker.121", $"превышено время ожидания") : Flatten(e);
            return new ConnectionStep(url.Host, false, message, sw.Elapsed);
        }
    }

    private static string Flatten(Exception e)
    {
        var parts = new List<string>();
        for (var x = e; x is not null; x = x.InnerException)
        {
            if (!parts.Contains(x.Message))
                parts.Add(x.Message);
        }

        return string.Join(" → ", parts);
    }

    private void Journal(ConnectionReport r) =>
        _journal?.Write("network", "check-connection", r.Ok ? "ok" : "error",
            details: new { mode = r.Proxy.Mode.ToString(), proxy = r.Proxy.ManualUri?.ToString(), steps = r.Steps.Select(s => $"{s.Target}: {(s.Ok ? "ok" : "ошибка")} — {s.Message}") });
}
