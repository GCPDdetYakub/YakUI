using System.Collections.Concurrent;
using YakUI.Core.Logging;

namespace YakUI.Core.Icons;

/// <summary>
/// Очередь загрузки иконок (FR-34b): не больше <c>maxParallel</c> одновременно (по умолчанию 4); запросы видимых строк
/// (<see cref="Request"/> с <c>visible: true</c>) обслуживаются раньше фоновых. Повторный запрос того же пакета
/// возвращает ту же задачу.
/// </summary>
public sealed class IconLoader
{
    private readonly IconService _service;
    private readonly IAppLog? _log;
    private readonly ConcurrentDictionary<string, Lazy<TaskCompletionSource<IconResult>>> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _started = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<IconRequest> _visible = new();
    private readonly ConcurrentQueue<IconRequest> _background = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task[] _workers;

    public IconLoader(IconService service, int maxParallel = 4, IAppLog? log = null)
    {
        _service = service;
        _log = log;
        _workers = Enumerable.Range(0, Math.Max(1, maxParallel)).Select(_ => Task.Run(WorkerAsync)).ToArray();
    }

    /// <summary>Иконка из кэша без ожидания; null — ещё неизвестна.</summary>
    public IconResult? TryGetCached(IconRequest request) =>
        _tasks.TryGetValue(Key(request), out var t) && t.IsValueCreated && t.Value.Task.IsCompletedSuccessfully
            ? t.Value.Task.Result
            : _service.GetCached(request);

    public Task<IconResult> Request(IconRequest request, bool visible)
    {
        var created = false;
        var lazy = _tasks.GetOrAdd(Key(request), _ => new Lazy<TaskCompletionSource<IconResult>>(() =>
        {
            created = true;
            return new TaskCompletionSource<IconResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }));
        var tcs = lazy.Value;

        if (created || visible)
        {
            // Видимую строку ставим и в приоритетную очередь, даже если она уже ждёт в фоновой.
            (visible ? _visible : _background).Enqueue(request);
            _signal.Release();
        }

        return tcs.Task;
    }

    private async Task WorkerAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_visible.TryDequeue(out var request) && !_background.TryDequeue(out request))
                continue;

            var tcs = _tasks[Key(request)].Value;
            // Запрос может лежать в обеих очередях (фон + видимая строка) — загружается один раз.
            if (tcs.Task.IsCompleted || !_started.TryAdd(Key(request), 0))
                continue;

            try
            {
                tcs.TrySetResult(await _service.GetAsync(request, _stop.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                tcs.TrySetCanceled();
                return;
            }
            catch (Exception e)
            {
                _log?.Warning($"Иконка {request.Name}: {e.Message}");
                tcs.TrySetResult(new IconResult(null, IconSource.Placeholder, null));
            }
        }
    }

    /// <summary>Сколько запросов ещё ждёт в очередях.</summary>
    public int Pending => _visible.Count + _background.Count;

    public async Task StopAsync()
    {
        _stop.Cancel();
        await Task.WhenAll(_workers).ConfigureAwait(false);
    }

    private static string Key(IconRequest r) => r.Name + "\n" + r.Version;
}
