using YakUI.Core.Cli;
using YakUI.Core.Platform;
using YakUI.Core.Versioning;

namespace YakUI.Core.Install;

/// <summary>Пакет в профиле Rhino: <c>&lt;профиль&gt;\&lt;Имя&gt;\&lt;Версия&gt;\</c> + <c>&lt;Имя&gt;\manifest.txt</c> (21.2).</summary>
public sealed record ProfilePackage(
    string Name,
    string Directory,
    string? ActiveVersion,
    IReadOnlyList<string> VersionDirectories,
    bool ManifestTxtMissing)
{
    /// <summary>Папки версий, кроме активной (FR-55: показывать отдельно).</summary>
    public IEnumerable<string> InactiveVersions =>
        VersionDirectories.Where(v => !string.Equals(v, ActiveVersion, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Сканирование профиля пакетов (FR-55, часть этапа 1 — только чтение).
/// Активная версия — из <c>manifest.txt</c>; если его нет и папка версии одна, она считается активной.
/// </summary>
public sealed class ProfileScanner
{
    private readonly IFileSystem _fs;

    public ProfileScanner(IFileSystem fs) => _fs = fs;

    public IReadOnlyList<ProfilePackage> Scan(string profileDirectory)
    {
        if (!_fs.DirectoryExists(profileDirectory))
            return [];

        var result = new List<ProfilePackage>();
        foreach (var packageDir in _fs.EnumerateDirectories(profileDirectory))
        {
            // Временные папки встроенного установщика YakUI (FR-50) — не пакеты.
            if (Path.GetFileName(packageDir).StartsWith(".yakui-", StringComparison.OrdinalIgnoreCase))
                continue;

            var versions = _fs.EnumerateDirectories(packageDir)
                .Select(Path.GetFileName)
                .OfType<string>()
                .OrderByDescending(v => PackageVersion.TryParse(v, out var pv) ? pv : null, Comparer<PackageVersion?>.Create(CompareNullable))
                .ToList();

            var manifest = Path.Combine(packageDir, "manifest.txt");
            var hasManifest = _fs.FileExists(manifest);
            var active = hasManifest ? YakOutputParser.ParseManifestTxt(_fs.ReadAllText(manifest)) : null;
            if (!hasManifest && versions.Count == 1)
                active = versions[0];

            if (versions.Count == 0 && !hasManifest)
                continue; // не пакет

            result.Add(new ProfilePackage(Path.GetFileName(packageDir), packageDir, active, versions, !hasManifest));
        }

        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int CompareNullable(PackageVersion? a, PackageVersion? b) =>
        a is null ? (b is null ? 0 : -1) : a.CompareTo(b);
}
