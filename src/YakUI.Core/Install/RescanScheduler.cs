namespace YakUI.Core.Install;

/// <summary>
/// Пересканирование профиля по событиям (FR-55a): «дребезг» — серия событий даёт один проход; не чаще
/// <c>minInterval</c>; на время собственных операций YakUI события копятся и дают один проход после них.
/// </summary>
public sealed class RescanScheduler : IDisposable
{
    private readonly Action _rescan;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _minInterval;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly object _gate = new();
    private DateTimeOffset _lastRun = DateTimeOffset.MinValue;
    private DateTimeOffset? _due;
    private int _suppressed;
    private bool _pending;

    public RescanScheduler(Action rescan, TimeSpan debounce, TimeSpan minInterval, TimeProvider? time = null)
    {
        _rescan = rescan;
        _debounce = debounce;
        _minInterval = minInterval;
        _time = time ?? TimeProvider.System;
        _timer = _time.CreateTimer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public int Runs { get; private set; }

    /// <summary>Изменение в профиле: проход после затишья <c>debounce</c>.</summary>
    public void Request() => Schedule(_debounce);

    /// <summary>Возврат фокуса окну или переполнение буфера: проход сразу, но с учётом лимита частоты.</summary>
    public void RequestNow() => Schedule(TimeSpan.Zero);

    /// <summary>Собственная операция YakUI: события до её окончания не вызывают проход, после — один проход.</summary>
    public IDisposable Suppress()
    {
        lock (_gate)
            _suppressed++;
        return new Releaser(this);
    }

    private void Schedule(TimeSpan delay)
    {
        lock (_gate)
        {
            if (_suppressed > 0)
            {
                _pending = true;
                return;
            }

            var now = _time.GetUtcNow();
            var due = now + delay;
            var earliest = _lastRun == DateTimeOffset.MinValue ? now : _lastRun + _minInterval;
            if (due < earliest)
                due = earliest;
            _due = due;
            _timer.Change(due - now, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_due is null || _suppressed > 0)
                return;
            _due = null;
            _lastRun = _time.GetUtcNow();
            Runs++;
        }

        _rescan();
    }

    private void Release()
    {
        bool run;
        lock (_gate)
        {
            _suppressed--;
            run = _suppressed == 0 && _pending;
            if (run)
                _pending = false;
        }

        if (run)
            Request();
    }

    public void Dispose() => _timer.Dispose();

    private sealed class Releaser(RescanScheduler owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                owner.Release();
        }
    }
}
