using System.Globalization;
using YakUI.Core.Compat;

namespace YakUI.Core.Targets;

public enum TargetSource
{
    Registry,
    ProgramFilesScan,
}

/// <summary>Установленный Rhino — цель установки пакетов (FR-01…FR-03).</summary>
public sealed record RhinoTarget
{
    /// <summary>Полная версия: <c>8.35.26251.13001</c>.</summary>
    public required Version Version { get; init; }

    public int Major => Version.Major;

    /// <summary>Сервис-релиз.</summary>
    public int Minor => Math.Max(Version.Minor, 0);

    public RhinoRelease Release => new(Major, Minor);

    public required string InstallPath { get; init; }

    public required string RhinoExePath { get; init; }

    public bool IsWip => ReleaseType == RhinoReleaseType.Wip;

    /// <summary>Тип выпуска (FR-81g); предварительные выпуски выделяются пометкой и цветом.</summary>
    public RhinoReleaseType ReleaseType { get; init; }

    public bool IsPrerelease => ReleaseType != RhinoReleaseType.ServiceRelease;

    /// <summary>Поля, по которым определялся тип (для <c>YakUI.Cli env</c>).</summary>
    public ReleaseEvidence? ReleaseEvidence { get; init; }

    public ReleaseTypeResult? ReleaseTypeMatch { get; init; }

    /// <summary>Платформа цели; на Windows всегда <c>win</c>.</summary>
    public string Platform => "win";

    /// <summary><c>N.0</c> — имя папки версии в профилях пакетов и реестре.</summary>
    public string ProfileKey => string.Create(CultureInfo.InvariantCulture, $"{Major}.0");

    /// <summary><c>%APPDATA%\McNeel\Rhinoceros\packages\&lt;N.0&gt;</c>.</summary>
    public required string UserPackagesDirectory { get; init; }

    /// <summary><c>%ProgramData%\McNeel\Rhinoceros\&lt;N.0&gt;\Packages</c> — только чтение (FR-02, ТЗ 0.2.2).</summary>
    public required string AllUsersPackagesDirectory { get; init; }

    public required string GrasshopperLibrariesDirectory { get; init; }

    /// <summary><c>&lt;Rhino&gt;\System\Yak.exe</c>, если есть.</summary>
    public string? BuiltInYakPath { get; init; }

    public required TargetRuntime Runtime { get; init; }

    public TargetSource Source { get; init; }

    /// <summary>«Rhino 8 SR35 (8.35.26251) · .NET 8» (FR-81a).</summary>
    public string DisplayName
    {
        get
        {
            var kind = ReleaseType switch
            {
                RhinoReleaseType.Wip => " WIP",
                RhinoReleaseType.Beta => string.Create(CultureInfo.InvariantCulture, $" SR{Minor} Beta"),
                RhinoReleaseType.ReleaseCandidate => string.Create(CultureInfo.InvariantCulture, $" SR{Minor} RC"),
                _ => string.Create(CultureInfo.InvariantCulture, $" SR{Minor}"),
            };
            return string.Create(CultureInfo.InvariantCulture, $"Rhino {Major}{kind} ({Major}.{Minor}.{Math.Max(Version.Build, 0)}) · {Runtime.DisplayName}");
        }
    }
}
