using System.Globalization;
using System.Text.RegularExpressions;
using YakUI.Core.Versioning;

namespace YakUI.Core.Cli;

/// <summary>Вывод <c>yak version</c>: <c>Yak 0.13.0 (0.13.9103.31475), Rhino 7.38 (7.38.24338.17001)</c>.</summary>
public sealed record YakVersionInfo(PackageVersion YakVersion, string? YakBuild, string? RhinoVersion, string? RhinoBuild);

public sealed record YakListEntry(string Name, string Version);

/// <summary>
/// Разобранный <c>yak list</c> (21.1). Из строки <c>Package directory:</c> берётся только окончание
/// <c>packages\&lt;N.0&gt;</c> (<see cref="PackageDirectorySuffix"/>): полный путь содержит имя пользователя Windows,
/// которое при чтении вывода yak может исказиться из-за кодировки.
/// </summary>
public sealed record YakListResult(string? PackageDirectorySuffix, int? BoundRhinoMajor, IReadOnlyList<YakListEntry> Packages);

public static partial class YakOutputParser
{
    public static YakVersionInfo? ParseVersion(string output)
    {
        foreach (var line in Lines(output))
        {
            var m = VersionRegex().Match(line);
            if (m.Success && PackageVersion.TryParse(m.Groups["yak"].Value, out var yak))
                return new YakVersionInfo(yak, Group(m, "yakBuild"), Group(m, "rhino"), Group(m, "rhinoBuild"));
        }

        return null;
    }

    public static YakListResult ParseList(string output)
    {
        string? suffix = null;
        int? major = null;
        var seenDirectory = false;
        var packages = new List<YakListEntry>();

        foreach (var line in Lines(output))
        {
            if (!seenDirectory && line.StartsWith("Package directory:", StringComparison.OrdinalIgnoreCase))
            {
                seenDirectory = true;
                (suffix, major) = ParseDirectorySuffix(line);
                continue;
            }

            var p = PackageLineRegex().Match(line);
            if (p.Success)
                packages.Add(new YakListEntry(p.Groups["name"].Value.Trim(), p.Groups["version"].Value));
        }

        return new YakListResult(suffix, major, packages);
    }

    /// <summary>
    /// <c>Package directory: …\packages\8.0</c> → (<c>packages\8.0</c>, 8). Смотрится только окончание строки,
    /// поэтому искажённые символы в имени пользователя не мешают. Иная раскладка — (null, null).
    /// </summary>
    public static (string? Suffix, int? Major) ParseDirectorySuffix(string line)
    {
        var m = DirectorySuffixRegex().Match(line.TrimEnd());
        if (!m.Success)
            return (null, null);

        var major = int.Parse(m.Groups["major"].Value, CultureInfo.InvariantCulture);
        return (string.Create(CultureInfo.InvariantCulture, $@"packages\{major}.0"), major);
    }

    /// <summary>Содержимое <c>&lt;Имя&gt;\manifest.txt</c>: одна строка — активная версия; перевода строки может не быть (21.2).</summary>
    public static string? ParseManifestTxt(string content)
    {
        var line = Lines(content.TrimStart('﻿')).FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? Group(Match m, string name) => m.Groups[name].Success ? m.Groups[name].Value : null;

    [GeneratedRegex(@"^Yak\s+(?<yak>\S+?)(?:\s+\((?<yakBuild>[^)]+)\))?(?:,\s*Rhino\s+(?<rhino>\S+?)(?:\s+\((?<rhinoBuild>[^)]+)\))?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"[\\/]packages[\\/](?<major>\d+)\.0[\\/]?$", RegexOptions.IgnoreCase)]
    private static partial Regex DirectorySuffixRegex();

    [GeneratedRegex(@"^(?<name>\S.*?)\s+\((?<version>[^()\s]+)\)$")]
    private static partial Regex PackageLineRegex();
}
