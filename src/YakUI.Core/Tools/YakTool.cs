using YakUI.Core.Cli;
using YakUI.Core.Platform;
using YakUI.Core.Versioning;

namespace YakUI.Core.Tools;

/// <summary>Откуда взят yak.exe (6.1).</summary>
public enum YakToolOrigin
{
    /// <summary>Встроенный в Rhino: <c>&lt;Rhino&gt;\System\Yak.exe</c>.</summary>
    BuiltIn,

    /// <summary>Скачанный YakUI: <c>%LOCALAPPDATA%\YakUI\yak\&lt;ver&gt;\yak.exe</c>.</summary>
    Downloaded,

    /// <summary>Указан пользователем вручную.</summary>
    Manual,
}

/// <summary>Решение по подлинности (FR-17).</summary>
public enum YakTrust
{
    /// <summary>Действительная подпись Robert McNeel &amp; Associates.</summary>
    SignedByMcNeel,

    /// <summary>Неподписанный, SHA-256 подтверждён пользователем.</summary>
    UnsignedAccepted,

    /// <summary>Неподписанный, скачан YakUI по HTTPS с files.mcneel.com, SHA-256 совпадает с записанным (FR-17).</summary>
    UnsignedDownloaded,

    /// <summary>Неподписанный без подтверждения: не запускается, пока пользователь не подтвердит.</summary>
    NeedsConfirmation,

    /// <summary>Подпись недействительна или издатель не McNeel.</summary>
    Rejected,
}

/// <summary>Один yak.exe в библиотеке (FR-13).</summary>
public sealed record YakTool
{
    public required string Path { get; init; }

    public required YakToolOrigin Origin { get; init; }

    public YakVersionInfo? VersionInfo { get; init; }

    public PackageVersion? Version => VersionInfo?.YakVersion;

    /// <summary>Окончание <c>packages\&lt;N.0&gt;</c> из <c>yak list</c>.</summary>
    public string? PackageDirectorySuffix { get; init; }

    /// <summary>Major-версия Rhino, к которой привязан yak; null — не определена.</summary>
    public int? BoundRhinoMajor { get; init; }

    public required SignatureInfo Signature { get; init; }

    public required YakTrust Trust { get; init; }

    public string? Sha256 { get; init; }

    public required RuntimeRequirement Runtime { get; init; }

    /// <summary>Почему yak нельзя использовать; null — пригоден.</summary>
    public string? Problem { get; init; }

    public bool IsUsable => Problem is null && BoundRhinoMajor is not null
        && Trust is YakTrust.SignedByMcNeel or YakTrust.UnsignedAccepted or YakTrust.UnsignedDownloaded;

    /// <summary>Глобальный ключ <c>--debug</c> есть с 0.15 (21.1). <c>--all-users</c> YakUI не использует (ТЗ 0.2.2).</summary>
    public bool SupportsDebugFlag => Version is { } v && (v.Major > 0 || v.Minor >= 15);
}

public sealed class YakBindingException : Exception
{
    public YakBindingException(string message) : base(message) { }
}
