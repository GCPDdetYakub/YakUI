using YakUI.Core.Api;
using YakUI.Core.Catalog;
using YakUI.Core.Compat;
using YakUI.Core.Downloads;
using YakUI.Core.Logging;
using YakUI.Core.Targets;
using YakUI.Core.Versioning;
using YakUI.Core.Localization;

namespace YakUI.Core.Install;

/// <summary>Кандидат на обновление: установленная версия и новее подходящая (FR-58, FR-59).</summary>
public sealed record UpdateCandidate(string Name, string InstalledVersion, string NewVersion, bool IsPinned);

public enum BulkOutcome
{
    Updated,
    Skipped,
    Failed,
    Cancelled,
}

public sealed record BulkItemResult(string Name, string? From, string? To, BulkOutcome Outcome, string? Message);

public sealed record BulkProgress(int Done, int Total, string Current);

/// <summary>Источник версий и файлов; в приложении и CLI — <see cref="CatalogService"/> и <see cref="Downloader"/>.</summary>
public interface IPackageFiles
{
    Task<IReadOnlyList<PackageVersionInfo>> GetVersionsAsync(string name, CancellationToken ct);

    /// <summary>Файл .yak дистрибутива: из папки загрузок или скачанный (FR-36, FR-37).</summary>
    Task<DownloadRecord> GetFileAsync(DownloadRequest request, CancellationToken ct);
}

/// <summary>
/// Жизненный цикл пакетов в стандартном профиле цели (этап 4): обновление и откат (FR-49, FR-58), закрепление (FR-58),
/// «Обновить всё» кроме закреплённых и выборочно (FR-59). Дистрибутив выбирается по разделу 17 для цели.
/// </summary>
public sealed class PackageLifecycle
{
    private readonly IPackageFiles _files;
    private readonly VersionChanger _changer;
    private readonly ProfileScanner _scanner;
    private readonly PinRegistry _pins;
    private readonly OperationJournal? _journal;

    public PackageLifecycle(IPackageFiles files, VersionChanger changer, ProfileScanner scanner, PinRegistry pins, OperationJournal? journal = null)
    {
        _files = files;
        _changer = changer;
        _scanner = scanner;
        _pins = pins;
        _journal = journal;
    }

    /// <summary>Самая новая не-prerelease версия, у которой есть дистрибутив, совместимый с целью (Mac-only и несовместимые — нет).</summary>
    public static (PackageVersionInfo Version, PackageDistribution Distribution)? BestFor(IReadOnlyList<PackageVersionInfo> versions, RhinoRelease target, bool allowPrerelease = false)
    {
        foreach (var v in versions.Where(v => allowPrerelease || !v.Prerelease).OrderByDescending(v => PackageVersion.TryParse(v.Version, out var p) ? p : null, NullableVersions))
        {
            if (Pick(v, target) is { } d)
                return (v, d);
        }

        return null;
    }

    /// <summary>Лучший дистрибутив версии для цели, только Нативный, Совместимый снизу или Без ограничений.</summary>
    public static PackageDistribution? Pick(PackageVersionInfo version, RhinoRelease target)
    {
        var d = CompatibilityRules.SelectBest(version.Distributions, x => x.Tag, target);
        if (d?.Url is null)
            return null;
        var c = CompatibilityRules.Evaluate(d.Tag, target);
        return c.PlatformMismatch || c.Status is CompatibilityStatus.Incompatible or CompatibilityStatus.Unknown ? null : d;
    }

    /// <summary>Пакеты профиля цели, для которых есть более новая подходящая версия; закреплённые помечены.</summary>
    public async Task<IReadOnlyList<UpdateCandidate>> FindUpdatesAsync(RhinoTarget target, CancellationToken ct = default)
    {
        var pins = _pins.ForTarget(StandardInstaller.TargetKey(target));
        var result = new List<UpdateCandidate>();
        foreach (var p in _scanner.Scan(target.UserPackagesDirectory).Where(p => p.ActiveVersion is not null))
        {
            IReadOnlyList<PackageVersionInfo> versions;
            try
            {
                versions = await _files.GetVersionsAsync(p.Name, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is CatalogUnavailableException or YakApiException or HttpRequestException)
            {
                continue; // нет в каталоге или сервер недоступен — обновлять нечем
            }

            if (BestFor(versions, target.Release) is not { } best
                || !PackageVersion.TryParse(best.Version.Version, out var newer)
                || !PackageVersion.TryParse(p.ActiveVersion!, out var current)
                || newer.CompareTo(current) <= 0)
                continue;
            result.Add(new UpdateCandidate(p.Name, p.ActiveVersion!, best.Version.Version, pins.ContainsKey(p.Name)));
        }

        return result;
    }

    /// <summary>
    /// Обновить или откатить пакет до версии (null — самая новая подходящая). Закрепление явной сменой версии
    /// переносится на новую версию: пользователь выбрал её сам.
    /// </summary>
    public async Task<VersionChangeResult> ChangeVersionAsync(RhinoTarget target, string name, string? version, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var versions = await _files.GetVersionsAsync(name, ct).ConfigureAwait(false);
        PackageVersionInfo info;
        PackageDistribution dist;
        if (version is null)
        {
            (info, dist) = BestFor(versions, target.Release) ?? throw new InstallFailedException(L.T("PackageLifecycle.87", $"У {name} нет версии, совместимой с {target.DisplayName}."));
        }
        else
        {
            info = versions.FirstOrDefault(v => BuiltInInstaller.SameVersion(v.Version, version))
                   ?? throw new InstallFailedException(L.T("PackageLifecycle.86", $"Версия {name} {version} не найдена в каталоге."));
            dist = Pick(info, target.Release) ?? throw new InstallFailedException(L.T("PackageLifecycle.85", $"У {name} {version} нет дистрибутива, совместимого с {target.DisplayName}."));
        }

        var file = await _files.GetFileAsync(new DownloadRequest(dist.Url!, dist.Filename ?? Path.GetFileName(new Uri(dist.Url!).LocalPath),
            name, info.Version, dist.Tag.Original, dist.Size, info.CreatedAt), ct).ConfigureAwait(false);
        var result = await _changer.ChangeAsync(new InstallRequest(target, name, info.Version, file.Path, dist.Tag, file.Sha256), ct).ConfigureAwait(false);

        var key = StandardInstaller.TargetKey(target);
        if (_pins.Find(key, name) is not null)
            _pins.Pin(key, result.Name, result.NewVersion);
        return result;
    }

    /// <summary>Закрепить активную версию (FR-58).</summary>
    public PinRecord Pin(RhinoTarget target, string name)
    {
        var p = _scanner.Scan(target.UserPackagesDirectory).FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InstallFailedException(L.T("PackageLifecycle.84", $"{name} не установлен в {target.DisplayName}."));
        var version = p.ActiveVersion ?? throw new InstallFailedException(L.T("PackageLifecycle.83", $"У {name} не определена активная версия."));
        var pin = _pins.Pin(StandardInstaller.TargetKey(target), p.Name, version);
        _journal?.Write("pin", $"{p.Name} {version} → Rhino {target.Major}", "pinned");
        return pin;
    }

    public void Unpin(RhinoTarget target, string name)
    {
        _pins.Unpin(StandardInstaller.TargetKey(target), name);
        _journal?.Write("pin", $"{name} → Rhino {target.Major}", "unpinned");
    }

    /// <summary>
    /// «Обновить всё» (FR-59): по очереди, закреплённые пропускаются; ошибка одного пакета не останавливает остальные,
    /// при ошибке у пакета остаётся прежняя версия (FR-49). <paramref name="only"/> — выборочно по галочкам.
    /// </summary>
    public async Task<IReadOnlyList<BulkItemResult>> UpdateAllAsync(RhinoTarget target, IReadOnlyCollection<string>? only = null,
        IProgress<BulkProgress>? progress = null, CancellationToken ct = default)
    {
        var candidates = (await FindUpdatesAsync(target, ct).ConfigureAwait(false))
            .Where(c => only is null || only.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var results = new List<BulkItemResult>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (ct.IsCancellationRequested)
            {
                results.Add(new BulkItemResult(c.Name, c.InstalledVersion, c.NewVersion, BulkOutcome.Cancelled, null));
                continue;
            }

            progress?.Report(new BulkProgress(i, candidates.Count, c.Name));
            if (c.IsPinned)
            {
                results.Add(new BulkItemResult(c.Name, c.InstalledVersion, c.NewVersion, BulkOutcome.Skipped, L.T("PackageLifecycle.82", $"версия закреплена")));
                continue;
            }

            try
            {
                var r = await ChangeVersionAsync(target, c.Name, c.NewVersion, null, ct).ConfigureAwait(false);
                results.Add(new BulkItemResult(c.Name, r.OldVersion, r.NewVersion, BulkOutcome.Updated, r.Warnings.Count > 0 ? string.Join(" ", r.Warnings) : null));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                results.Add(new BulkItemResult(c.Name, c.InstalledVersion, c.NewVersion, BulkOutcome.Cancelled, null));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                results.Add(new BulkItemResult(c.Name, c.InstalledVersion, c.NewVersion, BulkOutcome.Failed, e.Message));
            }
        }

        progress?.Report(new BulkProgress(candidates.Count, candidates.Count, ""));
        _journal?.Write("update-all", $"Rhino {target.Major}", results.Any(r => r.Outcome == BulkOutcome.Failed) ? "partial" : "ok",
            details: results.Select(r => $"{r.Name}: {r.Outcome} {r.From}→{r.To} {r.Message}"));
        return results;
    }

    private static readonly IComparer<PackageVersion?> NullableVersions =
        Comparer<PackageVersion?>.Create((a, b) => a is null ? (b is null ? 0 : -1) : a.CompareTo(b));
}
