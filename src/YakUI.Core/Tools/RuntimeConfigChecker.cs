using System.Globalization;
using System.Text.Json;
using YakUI.Core.Platform;
using YakUI.Core.Localization;

namespace YakUI.Core.Tools;

public sealed record RequiredFramework(string Name, Version Version, Version? Resolved)
{
    public bool Installed => Resolved is not null;
}

/// <summary>Результат FR-14a для одного yak.exe.</summary>
public sealed record RuntimeRequirement(bool FrameworkDependent, IReadOnlyList<RequiredFramework> Frameworks)
{
    public static RuntimeRequirement SelfContained { get; } = new(false, []);

    public bool IsSatisfied => Frameworks.All(f => f.Installed);

    /// <summary>Сообщение для пользователя, если среды не хватает; иначе null. Ничего не устанавливается.</summary>
    public string? Problem
    {
        get
        {
            var missing = Frameworks.Where(f => !f.Installed).ToList();
            if (missing.Count == 0)
                return null;

            // Desktop Runtime включает Microsoft.NETCore.App той же версии — пользователю достаточно его.
            var v = missing.Max(f => f.Version)!;
            var mm = string.Create(CultureInfo.InvariantCulture, $"{v.Major}.{Math.Max(v.Minor, 0)}");
            var names = string.Join(", ", missing.Select(f => $"{f.Name} {f.Version}"));
            return L.T("RuntimeConfigChecker.135", $"Установите .NET Desktop Runtime {v.Major} (нужно: {names}). Загрузка: https://dotnet.microsoft.com/download/dotnet/{mm}");
        }
    }
}

/// <summary>
/// FR-14a: если рядом с yak.exe лежит <c>*.runtimeconfig.json</c>, yak зависит от установленного .NET.
/// Из файла берутся имена фреймворков (<c>Microsoft.NETCore.App</c>, <c>Microsoft.WindowsDesktop.App</c>) и версии;
/// совместимость — по правилу roll-forward: тот же major, версия не ниже требуемой (minor не ниже).
/// </summary>
public sealed class RuntimeConfigChecker
{
    private readonly IFileSystem _fs;
    private readonly IDotNetRuntimeProbe _probe;

    public RuntimeConfigChecker(IFileSystem fs, IDotNetRuntimeProbe probe)
    {
        _fs = fs;
        _probe = probe;
    }

    public RuntimeRequirement Check(string yakPath)
    {
        var configPath = Path.Combine(
            Path.GetDirectoryName(yakPath) ?? ".",
            Path.GetFileNameWithoutExtension(yakPath) + ".runtimeconfig.json");
        if (!_fs.FileExists(configPath))
            return RuntimeRequirement.SelfContained;

        var frameworks = ReadFrameworks(_fs.ReadAllText(configPath));
        if (frameworks.Count == 0)
            return RuntimeRequirement.SelfContained;

        return new RuntimeRequirement(
            true,
            frameworks.Select(f => new RequiredFramework(f.Name, f.Version, Resolve(_probe.GetInstalledVersions(f.Name), f.Version))).ToList());
    }

    internal static IReadOnlyList<(string Name, Version Version)> ReadFrameworks(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var result = new List<(string, Version)>();
        if (!doc.RootElement.TryGetProperty("runtimeOptions", out var options))
            return result;

        if (options.TryGetProperty("frameworks", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in list.EnumerateArray())
                Add(f);
        }
        else if (options.TryGetProperty("framework", out var single))
        {
            Add(single);
        }

        return result;

        void Add(JsonElement f)
        {
            var name = f.TryGetProperty("name", out var n) ? n.GetString() : null;
            var version = f.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (name is not null && version is not null && Version.TryParse(version.Split('-')[0], out var parsed))
                result.Add((name, parsed));
        }
    }

    /// <summary>Roll-forward: тот же major и версия не ниже требуемой. Возвращает наибольшую подходящую.</summary>
    internal static Version? Resolve(IReadOnlyList<Version> installed, Version required) =>
        installed.Where(v => v.Major == required.Major && v >= required).OrderByDescending(v => v).FirstOrDefault();
}
