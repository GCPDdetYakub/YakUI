using YakUI.Core.Cli;
using YakUI.Core.Targets;
using YakUI.Core.Tools;
using YakUI.Core.Localization;

namespace YakUI.Core.Install;

public enum InstalledScope
{
    /// <summary>Профиль текущего пользователя: <c>%APPDATA%\McNeel\Rhinoceros\packages\&lt;N.0&gt;</c>.</summary>
    User,

    /// <summary>Профиль «для всех пользователей»: <c>%ProgramData%\McNeel\Rhinoceros\&lt;N.0&gt;\Packages</c>, только чтение.</summary>
    AllUsersReadOnly,

    /// <summary>Пользовательская папка с регистрацией (FR-42…FR-44).</summary>
    Custom,

    /// <summary>«Только распаковать» (FR-45).</summary>
    Unpacked,
}

public enum DiscrepancyKind
{
    /// <summary>Есть в профиле, нет в <c>yak list</c>.</summary>
    MissingInYakList,

    /// <summary>Есть в <c>yak list</c>, нет в профиле пользователя.</summary>
    MissingInProfile,

    /// <summary>Активная версия в <c>manifest.txt</c> отличается от <c>yak list</c>.</summary>
    VersionMismatch,
}

public sealed record Discrepancy(string Name, DiscrepancyKind Kind, string? ProfileVersion, string? YakVersion);

/// <summary>Установленное для одной цели (FR-55, этап 1 — только чтение).</summary>
public sealed record InstalledReport(
    RhinoTarget Target,
    IReadOnlyList<ProfilePackage> UserPackages,
    IReadOnlyList<ProfilePackage> AllUsersPackages,
    YakTool? Yak,
    YakListResult? YakList,
    string? YakListProblem,
    IReadOnlyList<Discrepancy> Discrepancies);

/// <summary>
/// Сводит сканирование профилей и <c>yak list</c> yak-а цели и подсвечивает расхождения (FR-55).
/// Без подходящего yak работает только сканирование (AD-10).
/// </summary>
public sealed class InstalledPackagesService
{
    private readonly ProfileScanner _scanner;
    private readonly YakToolLibrary _library;

    public InstalledPackagesService(ProfileScanner scanner, YakToolLibrary library)
    {
        _scanner = scanner;
        _library = library;
    }

    public async Task<InstalledReport> GetAsync(RhinoTarget target, CancellationToken ct = default)
    {
        var user = _scanner.Scan(target.UserPackagesDirectory);
        var allUsers = _scanner.Scan(target.AllUsersPackagesDirectory);

        var yak = _library.SelectFor(target.Major);
        YakListResult? list = null;
        string? problem = null;
        if (yak is null)
        {
            problem = L.T("InstalledPackagesService.81", $"Нет пригодного yak, привязанного к Rhino {target.Major}: показано только содержимое профиля.");
        }
        else
        {
            try
            {
                list = await _library.ListInstalledAsync(yak, target, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is YakCliException or YakBindingException)
            {
                problem = e.Message;
            }
        }

        return new InstalledReport(target, user, allUsers, yak, list, problem, list is null ? [] : Compare(user, list));
    }

    internal static IReadOnlyList<Discrepancy> Compare(IReadOnlyList<ProfilePackage> profile, YakListResult list)
    {
        var result = new List<Discrepancy>();
        var yakByName = list.Packages
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var profileNames = new HashSet<string>(profile.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var p in profile)
        {
            if (!yakByName.TryGetValue(p.Name, out var y))
                result.Add(new Discrepancy(p.Name, DiscrepancyKind.MissingInYakList, p.ActiveVersion, null));
            else if (!string.Equals(p.ActiveVersion, y.Version, StringComparison.OrdinalIgnoreCase))
                result.Add(new Discrepancy(p.Name, DiscrepancyKind.VersionMismatch, p.ActiveVersion, y.Version));
        }

        foreach (var y in list.Packages.Where(y => !profileNames.Contains(y.Name)))
            result.Add(new Discrepancy(y.Name, DiscrepancyKind.MissingInProfile, null, y.Version));

        return result;
    }
}
