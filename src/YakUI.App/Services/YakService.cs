using System.Globalization;
using YakUI.App.Resources;
using YakUI.Core.Logging;
using YakUI.Core.Settings;
using YakUI.Core.Targets;
using YakUI.Core.Tools;

namespace YakUI.App.Services;

/// <summary>
/// yak.exe в приложении (этап 4): поиск новых сборок (FR-12; при запуске — не чаще раза в сутки), скачивание с проверками
/// (FR-10, FR-14, FR-15, FR-17), переключение только когда нет других активных операций и запоминание прежнего yak для
/// «Вернуть предыдущий» (FR-19), хранение по 3 сборки на версию Rhino плюс закреплённые и используемые (FR-11).
/// </summary>
public sealed class YakService
{
    private readonly YakToolLibrary _library;
    private readonly Func<YakDownloader> _downloader;
    private readonly Func<YakUpdateChecker> _checker;
    private readonly BackgroundOperations _operations;
    private readonly AppSettings _settings;
    private readonly Action _save;
    private readonly Func<IReadOnlyList<RhinoTarget>> _targets;
    private readonly IAppLog? _log;
    private readonly TimeProvider _time;

    public YakService(YakToolLibrary library, Func<YakDownloader> downloader, Func<YakUpdateChecker> checker, BackgroundOperations operations,
        AppSettings settings, Action save, Func<IReadOnlyList<RhinoTarget>> targets, IAppLog? log = null, TimeProvider? time = null)
    {
        _library = library;
        _downloader = downloader;
        _checker = checker;
        _operations = operations;
        _settings = settings;
        _save = save;
        _targets = targets;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public YakCheckResult? LastCheck { get; private set; }

    /// <summary>Пауза ожидания конца других операций (FR-19); в тестах — короче.</summary>
    public TimeSpan IdlePoll { get; init; } = TimeSpan.FromSeconds(1);

    public event Action? Changed;

    private IEnumerable<int> Majors =>
        _targets().Select(t => t.Major).Concat(_library.Tools.Where(t => t.BoundRhinoMajor is not null).Select(t => t.BoundRhinoMajor!.Value)).Distinct();

    public async Task<YakCheckResult> CheckAsync(CancellationToken ct = default)
    {
        using var checker = _checker();
        var r = await checker.CheckAsync(_library.Tools, Majors, _library.SelectFor, ct).ConfigureAwait(false);
        LastCheck = r;
        _settings.LastYakCheck = _time.GetUtcNow();
        _save();
        foreach (var l in r.Lines.Where(l => l.HasUpdate))
            _log?.Info($"Доступен yak {l.Newer!.Version} для Rhino {l.RhinoMajor} (используется {l.Current?.Version}).");
        Changed?.Invoke();
        return r;
    }

    /// <summary>При запуске: не чаще раза в сутки (FR-12).</summary>
    public bool IsCheckDue => _settings.LastYakCheck is not { } last || _time.GetUtcNow() - last >= TimeSpan.FromDays(1);

    public async Task<string> DownloadAsync(string version, Func<bool> otherOperationsRunning, CancellationToken ct = default)
    {
        var before = _targets().Select(t => t.Major).Concat(Majors).Distinct().ToDictionary(m => m, m => _library.SelectFor(m)?.Path);

        YakDownloadResult r;
        using (var downloader = _downloader())
        {
            r = await downloader.DownloadAsync(version, null, ct).ConfigureAwait(false);

            // FR-19: переключение — только когда нет других активных операций.
            while (otherOperationsRunning())
                await Task.Delay(IdlePoll, ct).ConfigureAwait(false);

            await _library.RefreshAsync(_targets(), ct).ConfigureAwait(false);

            var keep = (_settings.PinnedYak ?? []).Values
                .Concat((_settings.PreviousYak ?? []).Values)
                .Concat(Majors.Select(m => _library.SelectFor(m)?.Path).OfType<string>())
                .Append(r.Path);
            if (downloader.Prune(_library.Tools, keep).Count > 0)
                await _library.RefreshAsync(_targets(), ct).ConfigureAwait(false);
        }

        var lines = new List<string>
        {
            string.Format(CultureInfo.CurrentCulture, Strings.Set_YakDownloaded, r.Version, r.Signed ? Strings.Set_YakDownloadedSigned : Strings.Set_YakDownloadedUnsigned,
                r.BoundRhinoMajor?.ToString(CultureInfo.InvariantCulture) ?? "?"),
        };
        if (r.BoundRhinoMajor is { } major)
        {
            var now = _library.SelectFor(major);
            var was = before.GetValueOrDefault(major);
            if (now is not null && !string.Equals(now.Path, was, StringComparison.OrdinalIgnoreCase))
            {
                if (was is not null)
                {
                    (_settings.PreviousYak ??= [])[major] = was;
                    _save();
                }

                lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.Set_YakSwitched, major, now.Version, was ?? "—"));
            }
            else if (now is not null)
            {
                lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.Set_YakNotSelected, major, now.Version));
            }
        }

        if (LastCheck is not null)
            LastCheck = LastCheck with { Lines = LastCheck.Lines.Select(l => l with { Current = _library.SelectFor(l.RhinoMajor), Newer = l.Newer is { } n && _library.SelectFor(l.RhinoMajor)?.Version is { } cv && n.Version.CompareTo(cv) > 0 ? n : null }).ToList() };
        Changed?.Invoke();
        return string.Join(Environment.NewLine, lines);
    }
}
