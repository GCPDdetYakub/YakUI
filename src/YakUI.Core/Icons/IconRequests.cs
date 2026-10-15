using YakUI.Core.Catalog;
using YakUI.Core.Compat;
using YakUI.Core.Versioning;

namespace YakUI.Core.Icons;

public static class IconRequests
{
    /// <summary>
    /// Запрос иконки для пакета каталога: последняя версия (не prerelease, если такие есть), её <c>icon_url</c>
    /// и файл <c>.yak</c> — предпочтительно не Mac-only. null — версии ещё не загружены (иконка позже).
    /// </summary>
    public static IconRequest? For(CatalogPackage package)
    {
        if (package.Versions.Count == 0)
            return null;

        var order = Comparer<PackageVersion?>.Create((a, b) => a is null ? (b is null ? 0 : -1) : a.CompareTo(b));
        var latest = package.Versions.Where(v => !v.Prerelease).MaxBy(v => v.Parsed, order)
            ?? package.Versions.MaxBy(v => v.Parsed, order)!;

        var file = latest.Files?.FirstOrDefault(f => f.Tag.Platform != DistributionPlatform.Mac && f.Url is not null)
            ?? latest.Files?.FirstOrDefault(f => f.Url is not null);

        return new IconRequest(package.Name, latest.Text, package.IconUrl, file?.Url, file?.Filename, package.FallbackIconUrl);
    }
}
