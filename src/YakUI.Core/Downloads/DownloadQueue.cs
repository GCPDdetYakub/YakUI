using System.Collections.Concurrent;

namespace YakUI.Core.Downloads;

public enum DownloadState
{
    Queued,
    Running,
    Verifying,
    Completed,

    /// <summary>Уже было скачано раньше — сеть не использовалась (FR-37).</summary>
    AlreadyDownloaded,

    Failed,
    Cancelled,
}

/// <summary>Элемент очереди загрузок (FR-38). Изменения состояния — через <see cref="DownloadQueue.Changed"/>.</summary>
public sealed class DownloadItem
{
    private CancellationTokenSource _cts = new();

    internal DownloadItem(DownloadRequest request) => Request = request;

    public DownloadRequest Request { get; }

    public DownloadState State { get; internal set; } = DownloadState.Queued;

    public long BytesDone { get; internal set; }

    public long? BytesTotal { get; internal set; }

    public string? Error { get; internal set; }

    public DownloadRecord? Record { get; internal set; }

    public bool IsFinished => State is DownloadState.Completed or DownloadState.AlreadyDownloaded or DownloadState.Failed or DownloadState.Cancelled;

    public bool CanRetry => State is DownloadState.Failed or DownloadState.Cancelled;

    internal CancellationToken Token => _cts.Token;

    internal void Cancel() => _cts.Cancel();

    internal void ResetForRetry()
    {
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        State = DownloadState.Queued;
        Error = null;
    }
}

/// <summary>
/// Очередь загрузок (FR-38): не больше <c>maxParallel</c> одновременно (по умолчанию 2), прогресс, отмена, повтор.
/// Повторная постановка той же ссылки, пока она в очереди или скачивается, возвращает существующий элемент.
/// Отменённая загрузка оставляет <c>.part</c> — повтор докачает (FR-37).
/// </summary>
public sealed class DownloadQueue
{
    private readonly Downloader _downloader;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<string, DownloadItem> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DownloadItem> _items = [];
    private readonly object _lock = new();

    public DownloadQueue(Downloader downloader, int maxParallel = 2)
    {
        _downloader = downloader;
        _slots = new SemaphoreSlim(Math.Max(1, maxParallel));
    }

    /// <summary>Состояние или прогресс элемента изменились (вызывается из фонового потока).</summary>
    public event Action<DownloadItem>? Changed;

    public IReadOnlyList<DownloadItem> Items
    {
        get
        {
            lock (_lock)
                return _items.ToList();
        }
    }

    /// <summary>Последняя запущенная задача элемента (для тестов и CLI).</summary>
    public Task Completion(DownloadItem item) => _tasks.TryGetValue(item, out var t) ? t : Task.CompletedTask;

    private readonly ConcurrentDictionary<DownloadItem, Task> _tasks = new();

    public DownloadItem Enqueue(DownloadRequest request)
    {
        var item = _active.GetOrAdd(request.Url, _ =>
        {
            var created = new DownloadItem(request);
            lock (_lock)
                _items.Insert(0, created);
            Start(created);
            return created;
        });
        return item;
    }

    public void Cancel(DownloadItem item)
    {
        if (!item.IsFinished)
            item.Cancel();
    }

    public void Retry(DownloadItem item)
    {
        if (!item.CanRetry || !_active.TryAdd(item.Request.Url, item))
            return;
        item.ResetForRetry();
        Changed?.Invoke(item);
        Start(item);
    }

    /// <summary>Убрать завершённые элементы из списка очереди (файлы в библиотеке остаются).</summary>
    public void ClearFinished()
    {
        lock (_lock)
            _items.RemoveAll(i => i.IsFinished);
    }

    private void Start(DownloadItem item)
    {
        Changed?.Invoke(item);
        _tasks[item] = Task.Run(() => RunAsync(item));
    }

    private async Task RunAsync(DownloadItem item)
    {
        try
        {
            await _slots.WaitAsync(item.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Finish(item, DownloadState.Cancelled, null);
            return;
        }

        try
        {
            if (_downloader.FindExisting(item.Request.Url) is { } existing)
            {
                item.Record = existing;
                item.BytesDone = existing.Size;
                item.BytesTotal = existing.Size;
                Finish(item, DownloadState.AlreadyDownloaded, null);
                return;
            }

            item.State = DownloadState.Running;
            Changed?.Invoke(item);
            var progress = new Progress(p =>
            {
                item.BytesDone = p.Done;
                item.BytesTotal = p.Total;
                if (p.Verifying)
                    item.State = DownloadState.Verifying;
                Changed?.Invoke(item);
            });

            item.Record = await _downloader.DownloadAsync(item.Request, progress, item.Token).ConfigureAwait(false);
            Finish(item, DownloadState.Completed, null);
        }
        catch (OperationCanceledException) when (item.Token.IsCancellationRequested)
        {
            Finish(item, DownloadState.Cancelled, null);
        }
        catch (DownloadFailedException e)
        {
            Finish(item, DownloadState.Failed, e.Message);
        }
        finally
        {
            _slots.Release();
        }
    }

    private void Finish(DownloadItem item, DownloadState state, string? error)
    {
        item.State = state;
        item.Error = error;
        _active.TryRemove(item.Request.Url, out _);
        Changed?.Invoke(item);
    }

    private sealed class Progress(Action<DownloadProgress> handler) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => handler(value);
    }
}
