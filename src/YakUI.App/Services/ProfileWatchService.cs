using YakUI.Core.Install;
using YakUI.Core.Logging;
using YakUI.Core.Platform;
using YakUI.Core.Targets;

namespace YakUI.App.Services;

/// <summary>
/// Наблюдение за профилями пакетов (FR-55a): изменения извне (Rhino, <c>_PackageManager</c>, другие программы)
/// дают пересканирование — «Установленные», метки каталога, реестр установок. «Дребезг» и лимит частоты —
/// <see cref="RescanScheduler"/>; переполнение буфера или ошибка наблюдателя и возврат фокуса окну — полный проход.
/// </summary>
public sealed class ProfileWatchService : IDisposable
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);

    private readonly IDirectoryWatcher _watcher;
    private readonly IAppLog? _log;
    private readonly RescanScheduler _scheduler;
    private readonly List<IDisposable> _watches = [];
    private readonly object _gate = new();

    public ProfileWatchService(IDirectoryWatcher watcher, Action rescan, IAppLog? log = null, TimeSpan? debounce = null, TimeSpan? minInterval = null)
    {
        _watcher = watcher;
        _log = log;
        _scheduler = new RescanScheduler(rescan, debounce ?? Debounce, minInterval ?? MinInterval);
    }

    public int Runs => _scheduler.Runs;

    /// <summary>Наблюдать за профилями пользователя этих целей (выбранная цель или все при «Любой версии»).</summary>
    public void WatchTargets(IEnumerable<RhinoTarget> targets)
    {
        lock (_gate)
        {
            foreach (var w in _watches)
                w.Dispose();
            _watches.Clear();
            foreach (var dir in targets.Select(t => t.UserPackagesDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (_watcher.Watch(dir, OnChanged) is { } w)
                    _watches.Add(w);
            }
        }
    }

    /// <summary>Возврат фокуса окну YakUI.</summary>
    public void OnActivated() => _scheduler.RequestNow();

    /// <summary>На время собственной установки/удаления события не вызывают проход; после — один проход.</summary>
    public IDisposable Suppress() => _scheduler.Suppress();

    private void OnChanged(bool overflow)
    {
        if (overflow)
        {
            _log?.Warning("Наблюдение за профилем: переполнение буфера или ошибка — полное пересканирование.");
            _scheduler.RequestNow();
        }
        else
        {
            _scheduler.Request();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var w in _watches)
                w.Dispose();
            _watches.Clear();
        }

        _scheduler.Dispose();
    }
}
