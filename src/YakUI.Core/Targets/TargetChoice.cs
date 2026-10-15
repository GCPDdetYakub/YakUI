using System.Globalization;
using YakUI.Core.Compat;

namespace YakUI.Core.Targets;

/// <summary>Тип пункта выпадающего списка Rhino (14.1).</summary>
public enum TargetChoiceKind
{
    /// <summary>Установленный Rhino (FR-81a).</summary>
    Installed,

    /// <summary>Вышедшая major-версия, которой нет на компьютере (FR-81b).</summary>
    NotInstalled,

    /// <summary>«Любая версия (без фильтра)» (FR-81d).</summary>
    AnyVersion,
}

/// <summary>Какие действия доступны для пункта списка Rhino (FR-81e).</summary>
public sealed record TargetCapabilities(
    bool CanBrowse,
    bool CanDownload,
    bool CanInstallStandard,
    bool CanInstallCustom,
    bool CanUseProfiles,
    bool ShowsInstalledSummaryForAllTargets)
{
    public static TargetCapabilities For(TargetChoiceKind kind) => kind switch
    {
        // Установленная — всё.
        TargetChoiceKind.Installed => new(true, true, true, true, true, false),

        // Неустановленная — просмотр, скачивание, пользовательская папка / «только распаковать», наборы; стандартная установка недоступна.
        TargetChoiceKind.NotInstalled => new(true, true, false, true, true, false),

        // «Любая версия» — просмотр и скачивание; установка недоступна; «Установленные» — сводка по всем целям.
        _ => new(true, true, false, false, false, true),
    };
}

/// <summary>
/// Пункт выпадающего списка Rhino. Для неустановленной версии <see cref="ServiceReleases"/> — minor-версии,
/// встреченные в тегах каталога (FR-81c); выбор «последний» = null.
/// </summary>
public sealed record TargetChoice
{
    public const string AnyKey = "any";

    private TargetChoice(TargetChoiceKind kind, string key, int? major, RhinoTarget? target, IReadOnlyList<int> serviceReleases)
    {
        Kind = kind;
        Key = key;
        Major = major;
        Target = target;
        ServiceReleases = serviceReleases;
        Capabilities = TargetCapabilities.For(kind);
    }

    public TargetChoiceKind Kind { get; }

    /// <summary>Устойчивый ключ для сохранения выбора (FR-81f): <c>installed:&lt;путь&gt;</c>, <c>major:9</c>, <c>any</c>.</summary>
    public string Key { get; }

    public int? Major { get; }

    /// <summary>Установленная цель (только для <see cref="TargetChoiceKind.Installed"/>).</summary>
    public RhinoTarget? Target { get; }

    /// <summary>Известные minor-версии, по возрастанию (только для неустановленной).</summary>
    public IReadOnlyList<int> ServiceReleases { get; }

    public TargetCapabilities Capabilities { get; }

    public static TargetChoice Installed(RhinoTarget target) =>
        new(TargetChoiceKind.Installed, "installed:" + target.InstallPath.TrimEnd('\\', '/').ToUpperInvariant(), target.Major, target, []);

    public static TargetChoice NotInstalled(int major, IReadOnlyList<int> serviceReleases) =>
        new(TargetChoiceKind.NotInstalled, string.Create(CultureInfo.InvariantCulture, $"major:{major}"), major, null, serviceReleases);

    public static TargetChoice Any { get; } = new(TargetChoiceKind.AnyVersion, AnyKey, null, null, []);

    /// <summary>
    /// Версия Rhino для фильтра совместимости: фактическая для установленной; выбранный (или последний известный)
    /// сервис-релиз для неустановленной; null — фильтр выключен (FR-81c, FR-81d).
    /// </summary>
    public RhinoRelease? ReleaseFor(int? selectedServiceRelease) => Kind switch
    {
        TargetChoiceKind.Installed => Target!.Release,
        TargetChoiceKind.NotInstalled => new RhinoRelease(
            Major!.Value,
            selectedServiceRelease is { } sr && ServiceReleases.Contains(sr) ? sr : ServiceReleases.Count > 0 ? ServiceReleases[^1] : 0),
        _ => null,
    };
}
