using YakUI.Core.Api;
using YakUI.Core.Compat;
using YakUI.Core.Versioning;

namespace YakUI.Core.Catalog;

/// <summary>Файл дистрибутива в снимке: метка, ссылка на .yak, имя файла.</summary>
public sealed record CatalogFile(DistributionTag Tag, string? Url, string? Filename);

/// <summary>Версия пакета в снимке: номер, prerelease, метки дистрибутивов, дата публикации, файлы.</summary>
public sealed record CatalogVersion(string Text, PackageVersion? Parsed, bool Prerelease, IReadOnlyList<DistributionTag> Tags, DateTimeOffset? CreatedAt = null, IReadOnlyList<CatalogFile>? Files = null);

/// <summary>Пакет каталога в памяти: сводка индекса, сведения о пакете и все версии с метками.</summary>
public sealed class CatalogPackage
{
    public CatalogPackage(
        PackageSummary summary,
        string? description,
        string? keywords,
        string? authors,
        string? homepageUrl,
        string? iconUrl,
        DateTimeOffset? createdAt,
        long downloadsLastMonth,
        IReadOnlyList<CatalogVersion> versions,
        bool isPending,
        string? fallbackIconUrl = null)
    {
        FallbackIconUrl = fallbackIconUrl;
        Summary = summary;
        Description = description;
        Keywords = keywords;
        Authors = authors ?? summary.Authors;
        HomepageUrl = homepageUrl;
        IconUrl = iconUrl;

        // FR-26: «дата создания» — публикация самой первой версии.
        CreatedAt = versions.Select(v => v.CreatedAt).Where(d => d is not null).Min() ?? createdAt;
        DownloadsLastMonth = downloadsLastMonth;
        Versions = versions;
        IsPending = isPending;

        // FR-23: Mac-only — у пакета есть дистрибутивы, и все они под mac.
        var tags = versions.SelectMany(v => v.Tags).ToList();
        IsMacOnly = tags.Count > 0 && tags.All(t => t.Platform == DistributionPlatform.Mac);

        NameWords = Catalog.SearchWords.Split(Name);
        SearchWords = [.. NameWords.Concat(SearchWordsOf(description, keywords, Authors)).Distinct(StringComparer.Ordinal)];
    }

    public PackageSummary Summary { get; }

    public string Name => Summary.Name;

    public string? Description { get; }

    public string? Keywords { get; }

    public string? Authors { get; }

    public string? HomepageUrl { get; }

    public string? IconUrl { get; }

    /// <summary><c>icon_url</c> самой новой версии, у которой он есть (FR-34: запасной источник иконки).</summary>
    public string? FallbackIconUrl { get; }

    /// <summary>Дата публикации первой версии (FR-26). <c>updated_at</c> индекса здесь не используется.</summary>
    public DateTimeOffset? CreatedAt { get; }

    public long DownloadCount => Summary.DownloadCount;

    public long DownloadsLastMonth { get; }

    public IReadOnlyList<CatalogVersion> Versions { get; }

    /// <summary>Версии не загружены или устарели: совместимость «уточняется» (FR-21).</summary>
    public bool IsPending { get; }

    public bool HasVersionData => Versions.Count > 0;

    public bool IsMacOnly { get; }

    /// <summary>Слова имени (FR-25: совпадения в имени выше).</summary>
    internal string[] NameWords { get; }

    /// <summary>Слова имени, описания, ключевых слов и авторов — для поиска по началу слова (FR-25).</summary>
    internal string[] SearchWords { get; }

    private static IEnumerable<string> SearchWordsOf(params string?[] parts) => parts.SelectMany(p => Catalog.SearchWords.Split(p));
}

/// <summary>Неизменяемый снимок каталога, загружаемый из кэша SQLite. Запросы к нему выполняются в памяти.</summary>
public sealed class CatalogSnapshot
{
    public CatalogSnapshot(IReadOnlyList<CatalogPackage> packages, DateTimeOffset? indexFetchedAt)
    {
        Packages = packages;
        IndexFetchedAt = indexFetchedAt;
        ByName = packages.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
    }

    public static CatalogSnapshot Empty { get; } = new([], null);

    public IReadOnlyList<CatalogPackage> Packages { get; }

    public IReadOnlyDictionary<string, CatalogPackage> ByName { get; }

    public DateTimeOffset? IndexFetchedAt { get; }

    public int PendingCount => Packages.Count(p => p.IsPending);
}
