using System.Text;
using YakUI.Core.Archive;
using YakUI.Core.Catalog;
using YakUI.Core.Compat;
using YakUI.Core.Platform;
using YakUI.Core.Targets;
using YakUI.Core.Localization;

namespace YakUI.Core.Install;

/// <summary>Статус установленного пакета (FR-56). «Ожидает удаления» появится после решения ОВ-04.</summary>
public enum InstalledStatus
{
    Active,

    /// <summary>Rhino цели запущен, а папка версии появилась после его старта (FR-05).</summary>
    PendingRestart,

    /// <summary>Активная версия не определена (нет <c>manifest.txt</c>, несколько папок версий).</summary>
    Unknown,
}

/// <summary>Строка экрана «Установленные» (FR-55, FR-56).</summary>
public sealed record InstalledItem
{
    public required RhinoTarget Target { get; init; }
    public required string Name { get; init; }
    public string? ActiveVersion { get; init; }
    public IReadOnlyList<string> InactiveVersions { get; init; } = [];
    public required InstalledScope Scope { get; init; }

    /// <summary>Папка пакета <c>…\&lt;Имя&gt;</c>.</summary>
    public required string PackageDirectory { get; init; }

    /// <summary>Папка активной версии <c>…\&lt;Имя&gt;\&lt;Версия&gt;</c>, если она есть.</summary>
    public string? VersionDirectory { get; init; }

    public YakManifest? Manifest { get; init; }
    public PackageContentKind ContentKind { get; init; }
    public InstalledStatus Status { get; init; }
    public bool ManifestTxtMissing { get; init; }

    /// <summary>Версия по <c>yak list</c>; null — yak её не показал или недоступен.</summary>
    public string? YakListVersion { get; init; }

    /// <summary>Расхождение профиля и <c>yak list</c> (FR-55: подсвечивается).</summary>
    public DiscrepancyKind? Discrepancy { get; init; }

    public bool IsReadOnly => Scope == InstalledScope.AllUsersReadOnly;

    /// <summary>Запись реестра для установок по пользовательскому пути (FR-46).</summary>
    public InstallRecord? CustomRecord { get; init; }

    /// <summary>FR-47: тот же пакет стоит и в профиле, и по пользовательскому пути для этой цели.</summary>
    public bool IsDuplicate { get; init; }

    /// <summary>Строка установки по пользовательскому пути из реестра (FR-46).</summary>
    public static InstalledItem FromCustom(RhinoTarget target, InstallRecord r) => new()
    {
        Target = target,
        Name = r.Name,
        ActiveVersion = r.Version,
        Scope = r.Mode == InstallMode.Unpack ? InstalledScope.Unpacked : InstalledScope.Custom,
        PackageDirectory = Path.GetDirectoryName(r.Path) ?? r.Path,
        VersionDirectory = r.Path,
        ContentKind = Directory.Exists(r.Path) ? Archive.PackageContentKindDetector.Detect(r.Path) : Archive.PackageContentKind.None,
        Status = Directory.Exists(r.Path) ? InstalledStatus.Active : InstalledStatus.Unknown,
        CustomRecord = r,
    };
}

/// <summary>Сводка по одной цели: строки, состояние yak list, запущенные процессы Rhino.</summary>
public sealed record InstalledOverview(
    RhinoTarget Target,
    IReadOnlyList<InstalledItem> Items,
    string? YakListProblem,
    IReadOnlyList<RunningRhino> Running);

/// <summary>Наличие и версия пакета в каталоге для цели (FR-56: «доступно обновление → X», FR-58: «показать в каталоге»).</summary>
public sealed record CatalogMatch(bool InCatalog, string? UpdateTo);

/// <summary>
/// Экран «Установленные» (FR-55…FR-58, FR-05): профиль пользователя и профиль «для всех» (только чтение),
/// <c>yak list</c> yak-а цели с расхождениями, данные из <c>manifest.yml</c> активной версии, тип содержимого по файлам,
/// статус «ожидает перезапуска», если Rhino цели запущен.
/// </summary>
public sealed class InstalledOverviewService
{
    private readonly IFileSystem _fs;
    private readonly InstalledPackagesService _installed;
    private readonly IRhinoProcessProbe _processes;

    public InstalledOverviewService(IFileSystem fs, InstalledPackagesService installed, IRhinoProcessProbe processes)
    {
        _fs = fs;
        _installed = installed;
        _processes = processes;
    }

    public async Task<InstalledOverview> GetAsync(RhinoTarget target, CancellationToken ct = default)
    {
        var report = await _installed.GetAsync(target, ct).ConfigureAwait(false);
        var running = _processes.FindRunning(target.InstallPath);
        var startedAt = running.Count == 0 ? (DateTimeOffset?)null : running.Min(r => r.StartTime);

        var yakByName = (report.YakList?.Packages ?? [])
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Version, StringComparer.OrdinalIgnoreCase);
        var discrepancies = report.Discrepancies.ToDictionary(d => d.Name, d => d.Kind, StringComparer.OrdinalIgnoreCase);

        var items = new List<InstalledItem>();
        items.AddRange(report.UserPackages.Select(p => Build(target, p, InstalledScope.User, startedAt, yakByName, discrepancies)));
        items.AddRange(report.AllUsersPackages.Select(p => Build(target, p, InstalledScope.AllUsersReadOnly, startedAt, null, null)));

        // Есть в yak list, но нет в профиле — тоже строка, чтобы расхождение было видно (FR-55).
        foreach (var d in report.Discrepancies.Where(d => d.Kind == DiscrepancyKind.MissingInProfile))
        {
            items.Add(new InstalledItem
            {
                Target = target,
                Name = d.Name,
                ActiveVersion = d.YakVersion,
                Scope = InstalledScope.User,
                PackageDirectory = Path.Combine(target.UserPackagesDirectory, d.Name),
                Status = InstalledStatus.Unknown,
                YakListVersion = d.YakVersion,
                Discrepancy = DiscrepancyKind.MissingInProfile,
            });
        }

        return new InstalledOverview(target, items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList(), report.YakListProblem, running);
    }

    private InstalledItem Build(
        RhinoTarget target,
        ProfilePackage p,
        InstalledScope scope,
        DateTimeOffset? rhinoStartedAt,
        IReadOnlyDictionary<string, string>? yakByName,
        IReadOnlyDictionary<string, DiscrepancyKind>? discrepancies)
    {
        var versionDir = p.ActiveVersion is { } v && _fs.DirectoryExists(Path.Combine(p.Directory, v)) ? Path.Combine(p.Directory, v) : null;

        YakManifest? manifest = null;
        var kind = PackageContentKind.None;
        var status = p.ActiveVersion is null ? InstalledStatus.Unknown : InstalledStatus.Active;
        if (versionDir is not null)
        {
            var manifestPath = Path.Combine(versionDir, "manifest.yml");
            if (_fs.FileExists(manifestPath))
            {
                try
                {
                    manifest = YakManifest.Parse(_fs.ReadAllText(manifestPath).TrimStart('﻿'));
                }
                catch (InvalidDataException)
                {
                    // повреждённый манифест — показываем без него
                }
            }

            kind = ContentKindOf(_fs.EnumerateFilesRecursive(versionDir));

            if (rhinoStartedAt is { } started && _fs.GetCreationTime(versionDir) is { } created && created > started)
                status = InstalledStatus.PendingRestart;
        }

        string? yakVersion = null;
        yakByName?.TryGetValue(p.Name, out yakVersion);
        DiscrepancyKind? discrepancy = discrepancies is not null && discrepancies.TryGetValue(p.Name, out var d) ? d : null;

        return new InstalledItem
        {
            Target = target,
            Name = p.Name,
            ActiveVersion = p.ActiveVersion,
            InactiveVersions = p.InactiveVersions.ToList(),
            Scope = scope,
            PackageDirectory = p.Directory,
            VersionDirectory = versionDir,
            Manifest = manifest,
            ContentKind = kind,
            Status = status,
            ManifestTxtMissing = p.ManifestTxtMissing,
            YakListVersion = yakVersion,
            Discrepancy = discrepancy,
        };
    }

    internal static PackageContentKind ContentKindOf(IEnumerable<string> files)
    {
        var rhp = false;
        var gh = false;
        foreach (var f in files)
        {
            rhp |= f.EndsWith(".rhp", StringComparison.OrdinalIgnoreCase);
            gh |= f.EndsWith(".gha", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ghpy", StringComparison.OrdinalIgnoreCase)
                  || f.EndsWith(".ghuser", StringComparison.OrdinalIgnoreCase);
            if (rhp && gh)
                break;
        }

        return (rhp, gh) switch
        {
            (true, true) => PackageContentKind.Mixed,
            (true, false) => PackageContentKind.Rhino,
            (false, true) => PackageContentKind.Grasshopper,
            _ => PackageContentKind.None,
        };
    }

    /// <summary>
    /// Пакет в каталоге и обновление для цели: «версия для цели» (самая новая совместимая) новее установленной.
    /// Пакеты, которых нет в каталоге, и цели без фильтра получают только признак наличия.
    /// </summary>
    public static CatalogMatch MatchCatalog(CatalogSnapshot snapshot, string name, string? installedVersion, RhinoRelease? target)
    {
        if (!snapshot.ByName.TryGetValue(name, out var package))
            return new CatalogMatch(false, null);

        // Имя в профиле и в каталоге может отличаться регистром.
        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (installedVersion is not null)
            installed[package.Name] = installedVersion;
        var entry = CatalogQuery.Evaluate(package, new CatalogFilter
        {
            Target = target,
            Statuses = Enum.GetValues<CompatibilityStatus>().ToHashSet(),
        }, installed);

        return new CatalogMatch(true, entry is { HasUpdate: true } ? entry.TargetVersion : null);
    }

    /// <summary>Подсказка: из чего собран статус (для всплывающей подсказки строки).</summary>
    public static string Describe(InstalledItem item)
    {
        var sb = new StringBuilder();
        sb.Append(item.VersionDirectory ?? item.PackageDirectory);
        if (item.InactiveVersions.Count > 0)
            sb.Append(L.T("InstalledOverview.80", $"\nнеактивные версии: ")).Append(string.Join(", ", item.InactiveVersions));
        if (item.ManifestTxtMissing)
            sb.Append(L.T("InstalledOverview.79", $"\nнет manifest.txt"));
        return sb.ToString();
    }
}
