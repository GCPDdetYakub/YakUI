using YakUI.Core.Targets;
using YakUI.Core.Versioning;

namespace YakUI.Core.Install;

/// <summary>
/// Установленные версии пакетов для фильтров «только установленные» / «есть обновление» (FR-27).
/// Источник — сканирование профилей (активная версия из <c>manifest.txt</c>). Полный список установленного
/// со сверкой с <c>yak list</c> — экран «Установленные» (шаг 2.7).
/// </summary>
public sealed class InstalledVersions
{
    private readonly ProfileScanner _scanner;

    public InstalledVersions(ProfileScanner scanner) => _scanner = scanner;

    /// <summary>
    /// Для установленной цели — её профиль пользователя и профиль «для всех» (только чтение);
    /// для «Любой версии» — все установленные цели (FR-81e), при нескольких версиях берётся самая новая;
    /// для неустановленной — пусто.
    /// </summary>
    public IReadOnlyDictionary<string, string> For(TargetChoice? choice, IEnumerable<RhinoTarget> installedTargets)
    {
        var targets = choice?.Kind switch
        {
            TargetChoiceKind.Installed => new List<RhinoTarget> { choice.Target! },
            TargetChoiceKind.AnyVersion => installedTargets.ToList(),
            _ => new List<RhinoTarget>(),
        };

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
        {
            foreach (var p in _scanner.Scan(t.UserPackagesDirectory).Concat(_scanner.Scan(t.AllUsersPackagesDirectory)))
            {
                if (p.ActiveVersion is null)
                    continue;
                if (!result.TryGetValue(p.Name, out var existing) || IsNewer(p.ActiveVersion, existing))
                    result[p.Name] = p.ActiveVersion;
            }
        }

        return result;
    }

    private static bool IsNewer(string candidate, string existing) =>
        PackageVersion.TryParse(candidate, out var c) && PackageVersion.TryParse(existing, out var e) && c > e;
}
