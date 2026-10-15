using System.Runtime.InteropServices;
using YakUI.Core.Platform;

namespace YakUI.Platform.Windows;

/// <summary>
/// Установленные среды .NET (FR-14a). Сначала реестр
/// <c>HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\&lt;arch&gt;\sharedfx\&lt;фреймворк&gt;</c> (имена значений — версии;
/// установщик .NET пишет их в 32-битное представление, поэтому смотрятся оба). Если там пусто —
/// папки <c>…\dotnet\shared\&lt;фреймворк&gt;\&lt;версия&gt;</c>.
/// </summary>
public sealed class WindowsDotNetRuntimeProbe : IDotNetRuntimeProbe
{
    private const string SetupKey = @"SOFTWARE\dotnet\Setup\InstalledVersions";

    private readonly IRegistryReader _registry;
    private readonly IFileSystem _fs;
    private readonly IReadOnlyList<string> _programFiles;
    private readonly string _arch;

    public WindowsDotNetRuntimeProbe(IRegistryReader registry, IFileSystem fs, IPlatformPaths paths, Architecture? osArchitecture = null)
    {
        _registry = registry;
        _fs = fs;
        _programFiles = paths.ProgramFilesDirectories;
        _arch = (osArchitecture ?? RuntimeInformation.OSArchitecture) switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };
    }

    public IReadOnlyList<Version> GetInstalledVersions(string frameworkName)
    {
        var fromRegistry = FromRegistry(frameworkName);
        var versions = fromRegistry.Count > 0 ? fromRegistry : FromFolders(frameworkName);
        return versions.OrderBy(v => v).ToList();
    }

    private HashSet<Version> FromRegistry(string frameworkName)
    {
        var result = new HashSet<Version>();
        var path = $@"{SetupKey}\{_arch}\sharedfx\{frameworkName}";
        foreach (var view in new[] { RegistryViewKind.Registry32, RegistryViewKind.Registry64 })
        {
            foreach (var name in _registry.GetValueNames(RegistryRoot.LocalMachine, path, view))
            {
                if (TryParseRuntimeVersion(name, out var v))
                    result.Add(v);
            }
        }

        return result;
    }

    private HashSet<Version> FromFolders(string frameworkName)
    {
        var roots = new List<string>();
        foreach (var view in new[] { RegistryViewKind.Registry32, RegistryViewKind.Registry64 })
        {
            var location = _registry.GetString(RegistryRoot.LocalMachine, $@"{SetupKey}\{_arch}", "InstallLocation", view);
            if (!string.IsNullOrWhiteSpace(location))
                roots.Add(location);
        }

        roots.AddRange(_programFiles.Select(p => Path.Combine(p, "dotnet")));

        var result = new HashSet<Version>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var dir in _fs.EnumerateDirectories(Path.Combine(root, "shared", frameworkName)))
            {
                if (TryParseRuntimeVersion(Path.GetFileName(dir), out var v))
                    result.Add(v);
            }
        }

        return result;
    }

    /// <summary><c>10.0.0-rc.1.25451.107</c> → 10.0.0.</summary>
    internal static bool TryParseRuntimeVersion(string text, out Version version)
    {
        var ok = Version.TryParse(text.Split('-', '+')[0], out var v);
        version = v ?? new Version();
        return ok;
    }
}
