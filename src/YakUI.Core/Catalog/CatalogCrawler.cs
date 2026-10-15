using System.Collections.Concurrent;
using System.Diagnostics;
using YakUI.Core.Api;
using YakUI.Core.Logging;

namespace YakUI.Core.Catalog;

/// <summary>Состояние обхода для индикатора в заголовке окна (FR-21).</summary>
public sealed record CrawlProgress(int Done, int Total, int Failed, int Concurrency, bool Throttled);

public sealed record CrawlResult(
    int IndexPackages,
    int Pending,
    int Fetched,
    int Failed,
    int ThrottleEvents,
    TimeSpan Duration,
    IReadOnlyList<string> FailedNames,
    bool IndexOffline = false);

public sealed record CrawlerOptions
{
    /// <summary>Максимум одновременных запросов (FR-20).</summary>
    public int MaxConcurrency { get; init; } = 6;

    /// <summary>Сколько успешных ответов подряд нужно, чтобы снова добавить один поток после замедления.</summary>
    public int SuccessesToSpeedUp { get; init; } = 25;

    /// <summary>Первая пауза при 429/503 без <c>Retry-After</c>; далее удваивается.</summary>
    public TimeSpan ThrottleBackoff { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxThrottleBackoff { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Попыток на пакет при 429/503.</summary>
    public int MaxThrottledAttempts { get; init; } = 6;

    /// <summary>Попыток на пакет при сетевых ошибках и 5xx (кроме 503).</summary>
    public int MaxErrorAttempts { get; init; } = 2;
}

/// <summary>
/// Фоновый обход <c>/versions/{name}</c> для всех пакетов индекса (FR-20, FR-21).
/// Обходятся только пакеты без кэша или с изменившимся <c>updated_at</c> — поэтому после перезапуска обход
/// продолжается с места остановки. Лимиты API неизвестны (ОВ-07): при 429/503 число потоков уменьшается вдвое
/// и все потоки ждут паузу (<c>Retry-After</c> или экспоненциальную), затем темп восстанавливается по одному потоку.
/// Пакеты, видимые в интерфейсе, можно поднять в начало очереди (<see cref="Prioritize"/>).
/// </summary>
public sealed class CatalogCrawler
{
    private readonly CatalogService _catalog;
    private readonly CrawlerOptions _options;
    private readonly IAppLog? _log;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private readonly ConcurrentQueue<string> _priority = new();
    private readonly object _lock = new();

    private int _concurrency;
    private int _inFlight;
    private int _successStreak;
    private int _consecutiveThrottles;
    private DateTimeOffset _pauseUntil;

    public CatalogCrawler(CatalogService catalog, CrawlerOptions? options = null, IAppLog? log = null, TimeProvider? time = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _catalog = catalog;
        _options = options ?? new CrawlerOptions();
        _log = log;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? Task.Delay;
        _concurrency = Math.Max(1, _options.MaxConcurrency);
    }

    /// <summary>Текущее допустимое число параллельных запросов (уменьшается при 429/503).</summary>
    public int CurrentConcurrency
    {
        get
        {
            lock (_lock)
                return _concurrency;
        }
    }

    /// <summary>Поднять пакеты в начало очереди (например, видимые строки каталога).</summary>
    public void Prioritize(IEnumerable<string> names)
    {
        foreach (var n in names)
            _priority.Enqueue(n);
    }

    public async Task<CrawlResult> RunAsync(IProgress<CrawlProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var index = await _catalog.GetIndexAsync(ct: ct).ConfigureAwait(false);
        _catalog.RemoveVersionsMissingFromIndex();

        var pending = _catalog.GetPendingPackages();
        var byName = pending.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var queue = new ConcurrentQueue<PackageSummary>(pending);
        var done = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var failed = new ConcurrentBag<string>();
        var attempts = new ConcurrentDictionary<string, (int Throttled, int Errors)>(StringComparer.OrdinalIgnoreCase);
        var throttleEvents = 0;
        var fetched = 0;
        var total = pending.Count;

        _log?.Info($"Краулер: в индексе {index.Value.Count} пакетов, к загрузке {total}{(index.IsOffline ? " (сервер недоступен — работа по кэшу)" : "")}.");
        Report();

        if (index.IsOffline || total == 0)
            return new CrawlResult(index.Value.Count, total, 0, 0, 0, sw.Elapsed, [], index.IsOffline);

        var workers = Enumerable.Range(0, Math.Max(1, _options.MaxConcurrency)).Select(_ => WorkerAsync()).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);

        sw.Stop();
        var result = new CrawlResult(index.Value.Count, total, fetched, failed.Count, throttleEvents, sw.Elapsed, failed.Order().ToList());
        _log?.Info($"Краулер: загружено {result.Fetched} из {total}, ошибок {result.Failed}, замедлений {result.ThrottleEvents}, время {result.Duration:mm\\:ss}.");
        return result;

        // Поток выходит, когда очередь пуста. Пакет, возвращённый в очередь после 429/503, заберёт тот же поток,
        // который его вернул: он продолжает цикл, поэтому ничего не теряется и никто не ждёт вечно.
        async Task WorkerAsync()
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await AcquireAsync(ct).ConfigureAwait(false);

                if (!TryTake(out var package))
                {
                    Release();
                    return;
                }

                try
                {
                    await _catalog.FetchAndStoreAsync(package, ct).ConfigureAwait(false);
                    done[package.Name] = true;
                    Interlocked.Increment(ref fetched);
                    OnSuccess();
                }
                catch (YakApiException e) when (e.IsThrottling)
                {
                    Interlocked.Increment(ref throttleEvents);
                    OnThrottled(e.RetryAfter);
                    var a = attempts.AddOrUpdate(package.Name, (1, 0), (_, x) => (x.Throttled + 1, x.Errors));
                    if (a.Throttled < _options.MaxThrottledAttempts)
                        queue.Enqueue(package);
                    else
                        Fail(package.Name, e);
                }
                catch (YakApiException e)
                {
                    var a = attempts.AddOrUpdate(package.Name, (0, 1), (_, x) => (x.Throttled, x.Errors + 1));
                    if (a.Errors < _options.MaxErrorAttempts && e.StatusCode is not System.Net.HttpStatusCode.NotFound)
                        queue.Enqueue(package);
                    else
                        Fail(package.Name, e);
                }
                finally
                {
                    Release();
                    Report();
                }
            }
        }

        bool TryTake(out PackageSummary package)
        {
            while (_priority.TryDequeue(out var name))
            {
                if (byName.TryGetValue(name, out var p) && !done.ContainsKey(name) && !failed.Contains(name))
                {
                    // Пакет может ещё лежать в обычной очереди — повтор отсеется проверкой done.
                    package = p;
                    return true;
                }
            }

            while (queue.TryDequeue(out var p))
            {
                if (!done.ContainsKey(p.Name))
                {
                    package = p;
                    return true;
                }
            }

            package = null!;
            return false;
        }

        void Fail(string name, Exception e)
        {
            failed.Add(name);
            done[name] = true;
            _log?.Warning($"Краулер: версии «{name}» не загружены: {e.Message}");
        }

        void Report() => progress?.Report(new CrawlProgress(
            done.Count, total, failed.Count, CurrentConcurrency, _time.GetUtcNow() < _pauseUntil));
    }

    /// <summary>Разрешение на запрос: ждёт конца общей паузы и места в текущем лимите запросов «в полёте».</summary>
    private async Task AcquireAsync(CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_lock)
            {
                var pause = _pauseUntil - _time.GetUtcNow();
                if (pause > TimeSpan.Zero)
                {
                    wait = pause;
                }
                else if (_inFlight < _concurrency)
                {
                    _inFlight++;
                    return;
                }
                else
                {
                    wait = TimeSpan.FromMilliseconds(50);
                }
            }

            await _delay(wait, ct).ConfigureAwait(false);
        }
    }

    private void Release()
    {
        lock (_lock)
            _inFlight--;
    }

    private void OnSuccess()
    {
        lock (_lock)
        {
            _consecutiveThrottles = 0;
            if (_concurrency >= _options.MaxConcurrency)
                return;
            if (++_successStreak >= _options.SuccessesToSpeedUp)
            {
                _concurrency++;
                _successStreak = 0;
                _log?.Info($"Краулер: темп восстанавливается, потоков {_concurrency}.");
            }
        }
    }

    private void OnThrottled(TimeSpan? retryAfter)
    {
        lock (_lock)
        {
            _successStreak = 0;
            _consecutiveThrottles++;
            _concurrency = Math.Max(1, _concurrency / 2);

            var backoff = retryAfter ?? TimeSpan.FromTicks(Math.Min(
                _options.MaxThrottleBackoff.Ticks,
                _options.ThrottleBackoff.Ticks << Math.Min(_consecutiveThrottles - 1, 10)));
            var until = _time.GetUtcNow() + backoff;
            if (until > _pauseUntil)
                _pauseUntil = until;

            _log?.Warning($"Краулер: сервер просит снизить нагрузку (429/503); потоков {_concurrency}, пауза {backoff.TotalSeconds:0.#} с.");
        }
    }
}
