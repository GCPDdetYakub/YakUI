using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using YakUI.Core.Localization;

namespace YakUI.Core.Api;

public sealed class YakApiException : Exception
{
    public YakApiException(string message, HttpStatusCode? statusCode = null, Exception? inner = null, TimeSpan? retryAfter = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public HttpStatusCode? StatusCode { get; }

    /// <summary>Заголовок <c>Retry-After</c>, если сервер его прислал.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Сервер просит снизить нагрузку: 429 или 503 (ОВ-07).</summary>
    public bool IsThrottling => StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
}

public sealed record YakApiOptions
{
    public Uri BaseUri { get; init; } = new("https://yak.rhino3d.com/");

    /// <summary>Лимит одновременных запросов (FR-20).</summary>
    public int MaxParallelism { get; init; } = 6;

    /// <summary>Число повторов после первой неудачи (NFR-03).</summary>
    public int MaxRetries { get; init; } = 3;

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Разрешить HTTP (только пользовательские серверы; публичный — только HTTPS, NFR-04).</summary>
    public bool AllowInsecure { get; init; }
}

/// <summary>
/// Клиент HTTP API Yak (AD-02): <c>/packages</c>, <c>/versions/{name}</c>, <c>/versions/{name}/{version}</c>,
/// <c>/packages/{name}/owners</c>. Повторы с экспоненциальной задержкой при сетевых ошибках, 408/429/5xx;
/// лимит параллелизма; системный прокси; <c>User-Agent: YakUI/&lt;ver&gt;</c> (NFR-03).
/// </summary>
public sealed class YakApiClient : IDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http;
    private readonly YakApiOptions _options;
    private readonly SemaphoreSlim _gate;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public YakApiClient(YakApiOptions? options = null, HttpMessageHandler? handler = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _options = options ?? new YakApiOptions();
        if (!_options.AllowInsecure && _options.BaseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException(L.T("YakApiClient.4", $"Сервер Yak — только HTTPS (NFR-04)."), nameof(options));

        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler
            {
                Proxy = Net.SwitchableProxy.Shared, // FR-95: режим из «Настроек», без перезапуска
                UseProxy = true,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            : new HttpClient(handler, disposeHandler: false);

        var baseUri = _options.BaseUri.AbsoluteUri;
        _http.BaseAddress = new Uri(baseUri.EndsWith('/') ? baseUri : baseUri + "/");
        _http.Timeout = _options.RequestTimeout;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YakUI", AppInfo.Version));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _gate = new SemaphoreSlim(Math.Max(1, _options.MaxParallelism));
        _delay = delay ?? Task.Delay;
    }

    public Task<IReadOnlyList<PackageSummary>> GetPackagesAsync(CancellationToken ct = default) =>
        GetListAsync<PackageSummary>("packages", ct);

    /// <param name="retryTransient">false — без внутренних повторов: вызывающий (краулер) сам управляет темпом (ОВ-07).</param>
    public Task<IReadOnlyList<PackageVersionInfo>> GetVersionsAsync(string name, CancellationToken ct = default, bool retryTransient = true) =>
        GetListAsync<PackageVersionInfo>($"versions/{Uri.EscapeDataString(name)}", ct, retryTransient ? _options.MaxRetries : 0);

    public async Task<PackageVersionInfo> GetVersionAsync(string name, string version, CancellationToken ct = default) =>
        await GetAsync<PackageVersionInfo>($"versions/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(version)}", ct).ConfigureAwait(false)
        ?? throw new YakApiException(L.T("YakApiClient.3", $"Пустой ответ для {name} {version}."));

    public Task<IReadOnlyList<PackageOwner>> GetOwnersAsync(string name, CancellationToken ct = default) =>
        GetListAsync<PackageOwner>($"packages/{Uri.EscapeDataString(name)}/owners", ct);

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string path, CancellationToken ct, int? maxRetries = null) =>
        await GetAsync<List<T>>(path, ct, maxRetries).ConfigureAwait(false) ?? [];

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct, int? maxRetries = null)
    {
        var retries = maxRetries ?? _options.MaxRetries;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var response = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false);
                    }

                    if (!IsTransient(response.StatusCode) || attempt >= retries)
                        throw new YakApiException($"{path}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}", response.StatusCode, retryAfter: RetryAfterOf(response));
                }
                catch (HttpRequestException) when (attempt < retries)
                {
                    // сетевая ошибка — повтор ниже
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < retries)
                {
                    // таймаут HttpClient — повтор ниже
                }
                catch (HttpRequestException e)
                {
                    throw new YakApiException($"{path}: {e.Message}", e.StatusCode, e);
                }
                catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
                {
                    throw new YakApiException(L.T("YakApiClient.2", $"{path}: превышено время ожидания."), null, e);
                }
                catch (JsonException e)
                {
                    throw new YakApiException(L.T("YakApiClient.1", $"{path}: неверный JSON ({e.Message})."), null, e);
                }

                await _delay(TimeSpan.FromTicks(_options.InitialRetryDelay.Ticks << attempt), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var h = response.Headers.RetryAfter;
        if (h?.Delta is { } delta)
            return delta;
        if (h?.Date is { } date)
            return date - DateTimeOffset.UtcNow is var d && d > TimeSpan.Zero ? d : TimeSpan.Zero;
        return null;
    }

    private static bool IsTransient(HttpStatusCode code) =>
        code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)code >= 500;

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
