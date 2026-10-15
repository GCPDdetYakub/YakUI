using System.Text.Json.Serialization;
using YakUI.Core.Localization;

namespace YakUI.Core.Compat;

public enum DotNetRuntimeKind
{
    /// <summary>.NET Framework (4.8).</summary>
    NetFramework,

    /// <summary>.NET (Core) 7/8/10…</summary>
    NetCore,
}

/// <summary>
/// Строка таблицы 17a: диапазон сервис-релизов одной major-версии Rhino и среда по умолчанию.
/// Хранится в файле настроек и обновляется без пересборки.
/// </summary>
public sealed record DotNetRuntimeRule
{
    public required int RhinoMajor { get; init; }

    /// <summary>Минимальный minor (включительно).</summary>
    public int MinMinor { get; init; }

    /// <summary>Максимальный minor (включительно); null — без верхней границы.</summary>
    public int? MaxMinor { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter<DotNetRuntimeKind>))]
    public required DotNetRuntimeKind Runtime { get; init; }

    /// <summary>Версия среды: «4.8», «7», «8», «10».</summary>
    public required string Version { get; init; }

    /// <summary>Доступен ли режим .NET Framework 4.8 (<c>/netfx</c>, <c>SetDotNetRuntime</c>).</summary>
    public bool NetFrameworkOptional { get; init; }

    public string? Note { get; init; }

    public bool Matches(RhinoRelease release) =>
        release.Major == RhinoMajor && release.Minor >= MinMinor && (MaxMinor is null || release.Minor <= MaxMinor);
}

/// <summary>Среда выполнения .NET цели (FR-02, 17a).</summary>
public sealed record TargetRuntime(DotNetRuntimeKind? Runtime, string? Version, bool NetFrameworkOptional, bool ModeDetermined)
{
    public bool IsKnown => Runtime is not null;

    public static TargetRuntime Unknown { get; } = new(null, null, false, false);

    /// <summary>«.NET Framework 4.8», «.NET 8», «среда неизвестна».</summary>
    public string DisplayName => Runtime switch
    {
        DotNetRuntimeKind.NetFramework => $".NET Framework {Version}",
        DotNetRuntimeKind.NetCore => $".NET {Version}",
        _ => L.T("DotNetRuntimeTable.36", $"среда неизвестна"),
    };
}

/// <summary>Таблица 17a. Значения по умолчанию — <see cref="Defaults"/>; рабочая копия берётся из настроек.</summary>
public sealed class DotNetRuntimeTable
{
    public DotNetRuntimeTable(IEnumerable<DotNetRuntimeRule> rules) => Rules = rules.ToList();

    public IReadOnlyList<DotNetRuntimeRule> Rules { get; }

    /// <summary>
    /// Среда цели по версии Rhino. Режим <c>SetDotNetRuntime</c> пока не читается (ОВ-17):
    /// где NetFx опционален, берётся среда по умолчанию (.NET Core) с <c>ModeDetermined = false</c>.
    /// </summary>
    public TargetRuntime Resolve(RhinoRelease release)
    {
        var rule = Rules.FirstOrDefault(r => r.Matches(release));
        return rule is null
            ? TargetRuntime.Unknown
            : new TargetRuntime(rule.Runtime, rule.Version, rule.NetFrameworkOptional, ModeDetermined: !rule.NetFrameworkOptional);
    }

    /// <summary>Таблица ТЗ 17a (2026-10-13).</summary>
    public static IReadOnlyList<DotNetRuntimeRule> Defaults { get; } =
    [
        new() { RhinoMajor = 6, Runtime = DotNetRuntimeKind.NetFramework, Version = "4.8", Note = "Уточнить при поддержке Rhino 6 (ОВ-03)" },
        new() { RhinoMajor = 7, Runtime = DotNetRuntimeKind.NetFramework, Version = "4.8" },
        new() { RhinoMajor = 8, MaxMinor = 19, Runtime = DotNetRuntimeKind.NetCore, Version = "7", NetFrameworkOptional = true },
        new() { RhinoMajor = 8, MinMinor = 20, MaxMinor = 35, Runtime = DotNetRuntimeKind.NetCore, Version = "8", NetFrameworkOptional = true },
        new() { RhinoMajor = 8, MinMinor = 36, Runtime = DotNetRuntimeKind.NetCore, Version = "10", NetFrameworkOptional = true },
        new() { RhinoMajor = 9, Runtime = DotNetRuntimeKind.NetCore, Version = "10", NetFrameworkOptional = true },
    ];
}
