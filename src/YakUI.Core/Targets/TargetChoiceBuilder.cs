using YakUI.Core.Compat;

namespace YakUI.Core.Targets;

/// <summary>Состав выпадающего списка Rhino и восстановление выбора (14.1, FR-81a…FR-81f).</summary>
public static class TargetChoiceBuilder
{
    /// <summary>
    /// Пункты в порядке показа: установленные (новые сверху) → неустановленные major (новые сверху) → «Любая версия».
    /// Major-версии берутся из настроек и тегов каталога (AD-07); Rhino 5 и ниже не показывается.
    /// </summary>
    public static IReadOnlyList<TargetChoice> Build(
        IReadOnlyList<RhinoTarget> installed,
        IEnumerable<int> configuredMajors,
        IEnumerable<RhinoRelease> releasesFromCatalog)
    {
        var releases = releasesFromCatalog.ToList();
        var result = new List<TargetChoice>();

        result.AddRange(installed.OrderByDescending(t => t.Version).Select(TargetChoice.Installed));

        var installedMajors = installed.Select(t => t.Major).ToHashSet();
        var configured = configuredMajors.ToList();
        var limit = PlausibleMajorLimit(configured, installedMajors);
        var majors = configured
            .Concat(releases.Select(r => r.Major).Where(m => m <= limit))
            .Where(m => m >= RhinoDiscovery.MinimumMajor && !installedMajors.Contains(m))
            .Distinct()
            .OrderByDescending(m => m);

        foreach (var major in majors)
        {
            var minors = releases.Where(r => r.Major == major).Select(r => r.Minor).Distinct().Order().ToList();
            result.Add(TargetChoice.NotInstalled(major, minors));
        }

        result.Add(TargetChoice.Any);
        return result;
    }

    /// <summary>
    /// Major-версии из тегов каталога, которых ещё нет в настройках (для пополнения списка, FR-81b).
    /// </summary>
    public static IReadOnlyList<int> NewMajors(IEnumerable<int> configuredMajors, IEnumerable<RhinoRelease> releasesFromCatalog, IEnumerable<int>? installedMajors = null)
    {
        var known = configuredMajors.ToHashSet();
        var limit = PlausibleMajorLimit(known, installedMajors ?? []);
        return releasesFromCatalog
            .Select(r => r.Major)
            .Where(m => m >= RhinoDiscovery.MinimumMajor && m <= limit && !known.Contains(m))
            .Distinct()
            .Order()
            .ToList();
    }

    /// <summary>
    /// Верхняя граница правдоподобной major-версии из тегов: самая новая известная (настройки и установленные) + 1.
    /// Отсекает опечатки авторов пакетов (<c>rh70_0</c>, <c>rh700_0</c> вместо <c>rh7_0</c>).
    /// </summary>
    public static int PlausibleMajorLimit(IEnumerable<int> configuredMajors, IEnumerable<int> installedMajors)
    {
        var known = configuredMajors.Concat(installedMajors).DefaultIfEmpty(RhinoDiscovery.MinimumMajor).Max();
        return known + 1;
    }

    /// <summary>
    /// Восстанавливает выбор (FR-81f): сохранённый ключ, если такой пункт есть; иначе самая новая установленная;
    /// если Rhino не установлен — «Любая версия».
    /// </summary>
    public static TargetChoice Restore(IReadOnlyList<TargetChoice> choices, string? savedKey)
    {
        if (savedKey is not null && choices.FirstOrDefault(c => c.Key == savedKey) is { } saved)
            return saved;

        return choices.FirstOrDefault(c => c.Kind == TargetChoiceKind.Installed) ?? TargetChoice.Any;
    }
}
