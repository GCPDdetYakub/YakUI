using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace YakUI.Core.Install;

/// <summary>Зависимости одиночного <c>.gha</c>/<c>.rhp</c>: что найдено рядом и чего не хватает (13a, FR-101).</summary>
public sealed record DependencyReport(IReadOnlyList<string> Found, IReadOnlyList<string> Missing)
{
    public static DependencyReport None { get; } = new([], []);
}

/// <summary>
/// Список сборок, нужных файлу, — только метаданные (<see cref="System.Reflection.Metadata"/>), сборка не загружается и не
/// выполняется (NFR-04). Системные, Rhino и Grasshopper отбрасываются; остальные ищутся рядом с исходным файлом, включая
/// зависимости найденных (транзитивно). Найденные можно скопировать вместе с файлом, ненайденные — предупреждение.
/// </summary>
public static class AssemblyDependencies
{
    private static readonly string[] HostPrefixes =
    [
        "System", "Microsoft.", "mscorlib", "netstandard", "WindowsBase", "PresentationCore", "PresentationFramework",
        "Accessibility", "UIAutomation", "RhinoCommon", "Rhino.", "RhinoWindows", "Grasshopper", "GH_IO", "GH_Util",
        "Eto", "Ed.Eto", "RevitAPI", "RevitAPIUI", "RhinoInside.", "Interop.", "stdole",
    ];

    /// <summary>Имена сборок, на которые ссылается файл; пусто — не .NET-сборка или файл не читается.</summary>
    public static IReadOnlyList<string> References(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return [];
            var md = pe.GetMetadataReader();
            return md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static bool IsHost(string assemblyName) =>
        HostPrefixes.Any(p => p.EndsWith('.') || p.Length == 0
            ? assemblyName.StartsWith(p, StringComparison.OrdinalIgnoreCase)
            : assemblyName.Equals(p, StringComparison.OrdinalIgnoreCase) || assemblyName.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase));

    /// <summary>Зависимости файла: найденные рядом <c>.dll</c> (полные пути) и недостающие (имена <c>.dll</c>).</summary>
    public static DependencyReport Analyze(string file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
        var found = new List<string>();
        var missing = new List<string>();
        var queue = new Queue<string>([file]);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileNameWithoutExtension(file) };
        while (queue.Count > 0 && seen.Count < 200)
        {
            foreach (var name in References(queue.Dequeue()).Where(n => !IsHost(n)))
            {
                if (!seen.Add(name))
                    continue;
                var candidate = Path.Combine(dir, name + ".dll");
                if (File.Exists(candidate))
                {
                    found.Add(candidate);
                    queue.Enqueue(candidate);
                }
                else
                {
                    missing.Add(name + ".dll");
                }
            }
        }

        return new DependencyReport(found, missing);
    }
}
