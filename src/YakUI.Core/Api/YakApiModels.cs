using System.Text.Json;
using YakUI.Core.Compat;

namespace YakUI.Core.Api;

/// <summary>Элемент индекса <c>GET /packages</c>.</summary>
public sealed record PackageSummary
{
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? Authors { get; init; }
    public long DownloadCount { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? Url { get; init; }
}

/// <summary>Версия пакета: элемент <c>GET /versions/{name}</c> или ответ <c>GET /versions/{name}/{version}</c>.</summary>
public sealed record PackageVersionInfo
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? Authors { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public string? Description { get; init; }
    public string? HomepageUrl { get; init; }
    public string? IconUrl { get; init; }
    public List<string> Keywords { get; init; } = [];
    public bool Prerelease { get; init; }

    /// <summary>Формат зависимостей в API не зафиксирован — хранится как есть.</summary>
    public JsonElement? Dependencies { get; init; }

    public long DownloadCount { get; init; }
    public DownloadStats? Downloads { get; init; }
    public List<PackageDistribution> Distributions { get; init; } = [];
    public string? Url { get; init; }
}

public sealed record DownloadStats
{
    public long LastDay { get; init; }
    public long LastWeek { get; init; }
    public long LastMonth { get; init; }
}

/// <summary>Дистрибутив версии (<c>distributions[]</c>).</summary>
public sealed record PackageDistribution
{
    public string? Filename { get; init; }
    public string? Platform { get; init; }
    public string? RhinoVersion { get; init; }
    public long? Size { get; init; }
    public string? Url { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }

    public DistributionTag Tag => DistributionTag.FromApi(RhinoVersion, Platform);
}

public sealed record PackageOwner
{
    public long Id { get; init; }
    public string? Name { get; init; }
}
