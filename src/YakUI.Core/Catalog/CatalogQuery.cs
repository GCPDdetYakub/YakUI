using YakUI.Core.Compat;
using YakUI.Core.Versioning;

namespace YakUI.Core.Catalog;

/// <summary>Сортировки каталога (FR-26).</summary>
public enum CatalogSort
{
    Name,
    Downloads,
    DownloadsLastMonth,
    Updated,
    Created,
}

/// <summary>Фильтр каталога (FR-22…FR-27). <see cref="Target"/> = null — «Любая версия», фильтр совместимости выключен.</summary>
public sealed record CatalogFilter
{
    public string? Text { get; init; }

    public RhinoRelease? Target { get; init; }

    /// <summary>
    /// Показываемые статусы (FR-22), объединяются через ИЛИ. Пусто — фильтра по совместимости нет (все пакеты).
    /// По умолчанию — всё, кроме Несовместимых.
    /// </summary>
    public IReadOnlySet<CompatibilityStatus> Statuses { get; init; } = DefaultStatuses;

    /// <summary>
    /// Группа «Платформа» (FR-22): отмеченные платформы объединяются через ИЛИ; пусто — фильтра по платформе нет.
    /// Платформа — по дистрибутивам версии, показанной в строке каталога; <c>any</c> подходит обеим.
    /// По умолчанию — только Windows.
    /// </summary>
    public IReadOnlySet<CatalogPlatform> Platforms { get; init; } = DefaultPlatforms;

    /// <summary>
    /// «Скрыть пре-релизы» (FR-22, FR-27): рассматриваются только стабильные версии, пакеты только с пре-релизами скрыты
    /// (как в PackageManager при снятой «Include pre-releases»). false — все версии, версия для цели может быть пре-релизом.
    /// </summary>
    public bool HidePrerelease { get; init; } = true;

    public bool OnlyInstalled { get; init; }

    public bool OnlyWithUpdate { get; init; }

    public CatalogSort Sort { get; init; } = CatalogSort.Downloads;

    public bool Descending { get; init; } = true;

    public static IReadOnlySet<CatalogPlatform> DefaultPlatforms { get; } = new HashSet<CatalogPlatform> { CatalogPlatform.Windows };

    /// <summary>Все галочки сняты — полный каталог (FR-22).</summary>
    public static CatalogFilter Everything { get; } = new()
    {
        Statuses = new HashSet<CompatibilityStatus>(),
        Platforms = new HashSet<CatalogPlatform>(),
        HidePrerelease = false,
    };

    public static IReadOnlySet<CompatibilityStatus> DefaultStatuses { get; } = new HashSet<CompatibilityStatus>
    {
        CompatibilityStatus.Native,
        CompatibilityStatus.BackwardCompatible,
        CompatibilityStatus.Unrestricted,
        CompatibilityStatus.Unknown,
    };
}

/// <summary>Платформа в фильтре каталога (FR-22).</summary>
public enum CatalogPlatform
{
    Windows,
    Mac,
}

/// <summary>
/// Строка результата: пакет, статус для цели, версия для цели, установленная версия и «дата обновления» —
/// публикация самой новой версии с учётом переключателя prerelease (FR-26).
/// </summary>
public sealed record CatalogEntry(
    CatalogPackage Package,
    CompatibilityStatus? Status,
    string? TargetVersion,
    bool TargetVersionIsPrerelease,
    string? InstalledVersion,
    bool HasUpdate,
    DateTimeOffset? UpdatedAt = null)
{
    /// <summary>Совместимость «уточняется»: версии не загружены или устарели (FR-21).</summary>
    public bool IsPending => Package.IsPending;
}

public sealed record CatalogQueryResult(IReadOnlyList<CatalogEntry> Entries, int TotalPackages, int PendingPackages);

/// <summary>
/// Запрос к снимку каталога. Статус пакета для цели — лучший статус среди рассматриваемых версий (17.6);
/// prerelease-версии рассматриваются только при включённом переключателе (FR-27). «Версия для цели» —
/// самая новая версия с совместимым статусом (Нативный / Совместимый снизу / Без ограничений).
/// Пакеты, у которых версии ещё не загружены, показываются с пометкой «уточняется» независимо от фильтра статусов.
/// </summary>
public static class CatalogQuery
{
    private static readonly Comparer<PackageVersion?> VersionOrder =
        Comparer<PackageVersion?>.Create((a, b) => a is null ? (b is null ? 0 : -1) : a.CompareTo(b));

    public static CatalogQueryResult Run(
        CatalogSnapshot snapshot,
        CatalogFilter filter,
        IReadOnlyDictionary<string, string>? installed = null)
    {
        installed ??= new Dictionary<string, string>();
        // FR-25: по началу слова; все слова запроса должны найтись.
        var terms = SearchWords.Terms(filter.Text);

        var entries = new List<CatalogEntry>();
        foreach (var p in snapshot.Packages)
        {
            if (terms.Length > 0 && !SearchWords.MatchesAll(p.SearchWords, terms))
                continue;

            var entry = Evaluate(p, filter, installed);
            if (entry is null)
                continue;

            if (filter.OnlyInstalled && entry.InstalledVersion is null)
                continue;
            if (filter.OnlyWithUpdate && !entry.HasUpdate)
                continue;

            entries.Add(entry);
        }

        return new CatalogQueryResult(Sort(entries, filter, terms), snapshot.Packages.Count, snapshot.PendingCount);
    }

    /// <summary>Статус и версия пакета для фильтра; null — пакет скрыт фильтром.</summary>
    public static CatalogEntry? Evaluate(CatalogPackage p, CatalogFilter filter, IReadOnlyDictionary<string, string> installed)
    {
        installed.TryGetValue(p.Name, out var installedVersion);

        if (!p.HasVersionData)
        {
            // Версии ещё не загружены: показываем с пометкой «уточняется» (FR-21).
            return new CatalogEntry(p, null, p.Summary.Version, false, installedVersion, false, null);
        }

        var considered = filter.HidePrerelease ? p.Versions.Where(v => !v.Prerelease).ToList() : p.Versions.ToList();
        if (considered.Count == 0)
            return null; // только пре-релизы, а они скрыты

        CompatibilityStatus? status = null;
        CatalogVersion? targetVersion;
        if (filter.Target is { } target)
        {
            var statuses = considered
                .Select(v => (Version: v, Status: CompatibilityRules.EvaluateVersion(v.Tags, target)))
                .ToList();
            status = statuses.MinBy(s => CompatibilityRules.Rank(s.Status)).Status;

            // Ни одного статуса не отмечено — фильтра по совместимости нет.
            if (!p.IsPending && filter.Statuses.Count > 0 && !filter.Statuses.Contains(status.Value))
                return null;

            targetVersion = statuses
                .Where(s => s.Status is CompatibilityStatus.Native or CompatibilityStatus.BackwardCompatible or CompatibilityStatus.Unrestricted)
                .Select(s => s.Version)
                .MaxBy(v => v.Parsed, VersionOrder);
            targetVersion ??= considered.MaxBy(v => v.Parsed, VersionOrder);
        }
        else
        {
            targetVersion = considered.MaxBy(v => v.Parsed, VersionOrder);
        }

        // Платформа — по дистрибутивам показанной версии (FR-22); ИЛИ внутри группы, пусто — без фильтра.
        if (filter.Platforms.Count > 0 && targetVersion is not null && !filter.Platforms.Any(pl => Supports(targetVersion, pl)))
            return null;

        var compatible = status is null or CompatibilityStatus.Native or CompatibilityStatus.BackwardCompatible or CompatibilityStatus.Unrestricted;
        var hasUpdate = compatible
            && installedVersion is not null
            && PackageVersion.TryParse(installedVersion, out var iv)
            && targetVersion?.Parsed is { } tv
            && tv > iv;

        var updatedAt = considered.Select(v => v.CreatedAt).Where(d => d is not null).Max();
        return new CatalogEntry(p, status, targetVersion?.Text, targetVersion?.Prerelease ?? false, installedVersion, hasUpdate, updatedAt);
    }

    /// <summary>
    /// Версия подходит платформе: нет меток (= any), метка <c>any</c> или этой платформы. Неизвестная платформа
    /// не скрывает пакет (NFR-06).
    /// </summary>
    internal static bool Supports(CatalogVersion v, CatalogPlatform platform) =>
        v.Tags.Count == 0 || v.Tags.Any(t => t.Platform switch
        {
            DistributionPlatform.Any or DistributionPlatform.Unknown => true,
            DistributionPlatform.Win => platform == CatalogPlatform.Windows,
            DistributionPlatform.Mac => platform == CatalogPlatform.Mac,
            _ => true,
        });

    private static List<CatalogEntry> Sort(List<CatalogEntry> entries, CatalogFilter filter, string[] terms)
    {
        IOrderedEnumerable<CatalogEntry> ordered;

        // FR-25: точное имя, затем все слова в имени, затем остальные; внутри групп — выбранная сортировка.
        if (terms.Length > 0)
        {
            var query = string.Join(' ', terms);
            ordered = entries.OrderBy(e =>
                string.Join(' ', e.Package.NameWords) == query || e.Package.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0
                : SearchWords.MatchesAll(e.Package.NameWords, terms) ? 1
                : 2);
            ordered = Then(ordered, filter);
        }
        else
        {
            ordered = First(entries, filter);
        }

        return ordered.ThenBy(e => e.Package.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IOrderedEnumerable<CatalogEntry> First(IEnumerable<CatalogEntry> e, CatalogFilter f) => (f.Sort, f.Descending) switch
    {
        (CatalogSort.Name, false) => e.OrderBy(x => x.Package.Name, StringComparer.OrdinalIgnoreCase),
        (CatalogSort.Name, true) => e.OrderByDescending(x => x.Package.Name, StringComparer.OrdinalIgnoreCase),
        (CatalogSort.Downloads, false) => e.OrderBy(x => x.Package.DownloadCount),
        (CatalogSort.Downloads, true) => e.OrderByDescending(x => x.Package.DownloadCount),
        (CatalogSort.DownloadsLastMonth, false) => e.OrderBy(x => x.Package.DownloadsLastMonth),
        (CatalogSort.DownloadsLastMonth, true) => e.OrderByDescending(x => x.Package.DownloadsLastMonth),
        (CatalogSort.Updated, false) => e.OrderBy(x => x.UpdatedAt),
        (CatalogSort.Updated, true) => e.OrderByDescending(x => x.UpdatedAt),
        (CatalogSort.Created, false) => e.OrderBy(x => x.Package.CreatedAt),
        _ => e.OrderByDescending(x => x.Package.CreatedAt),
    };

    private static IOrderedEnumerable<CatalogEntry> Then(IOrderedEnumerable<CatalogEntry> e, CatalogFilter f) => (f.Sort, f.Descending) switch
    {
        (CatalogSort.Name, false) => e.ThenBy(x => x.Package.Name, StringComparer.OrdinalIgnoreCase),
        (CatalogSort.Name, true) => e.ThenByDescending(x => x.Package.Name, StringComparer.OrdinalIgnoreCase),
        (CatalogSort.Downloads, false) => e.ThenBy(x => x.Package.DownloadCount),
        (CatalogSort.Downloads, true) => e.ThenByDescending(x => x.Package.DownloadCount),
        (CatalogSort.DownloadsLastMonth, false) => e.ThenBy(x => x.Package.DownloadsLastMonth),
        (CatalogSort.DownloadsLastMonth, true) => e.ThenByDescending(x => x.Package.DownloadsLastMonth),
        (CatalogSort.Updated, false) => e.ThenBy(x => x.UpdatedAt),
        (CatalogSort.Updated, true) => e.ThenByDescending(x => x.UpdatedAt),
        (CatalogSort.Created, false) => e.ThenBy(x => x.Package.CreatedAt),
        _ => e.ThenByDescending(x => x.Package.CreatedAt),
    };
}
