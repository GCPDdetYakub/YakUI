using System.Globalization;
using System.Text.RegularExpressions;
using YakUI.Core.Compat;
using YakUI.Core.Platform;

namespace YakUI.Core.Targets;

/// <summary>
/// Поиск установленных Rhino 6+ (FR-01…FR-03).
/// Основной источник — <c>HKLM\SOFTWARE\McNeel\Rhinoceros\&lt;N.0&gt;\Install</c> (InstallPath, Version),
/// читается явно в 64-битном представлении, с проверкой <c>Rhino.exe</c>.
/// Запасной путь — <c>Program Files\Rhino *</c>: блок реестра в одном из прогонов этапа 0 был пуст (21.2),
/// и реестр хранит лишь одну установку на major-версию (вторая сборка, например WIP, находится сканированием).
/// </summary>
public sealed partial class RhinoDiscovery
{
    public const int MinimumMajor = 6;
    private const string RhinoKey = @"SOFTWARE\McNeel\Rhinoceros";

    private readonly IRegistryReader _registry;
    private readonly IFileSystem _fs;
    private readonly IPlatformPaths _paths;
    private readonly DotNetRuntimeTable _runtimes;

    private readonly ReleaseTypeRules _releaseRules;

    public RhinoDiscovery(IRegistryReader registry, IFileSystem fs, IPlatformPaths paths, DotNetRuntimeTable runtimes, IEnumerable<ReleaseTypeRule>? releaseRules = null)
    {
        _releaseRules = new ReleaseTypeRules(releaseRules ?? ReleaseTypeRules.Defaults);
        _registry = registry;
        _fs = fs;
        _paths = paths;
        _runtimes = runtimes;
    }

    /// <summary>Цели от новой к старой; одна папка установки — одна цель.</summary>
    public IReadOnlyList<RhinoTarget> Discover()
    {
        var byPath = new Dictionary<string, RhinoTarget>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in FromRegistry().Concat(FromProgramFiles()))
            byPath.TryAdd(NormalizeDir(target.InstallPath), target);

        return byPath.Values.OrderByDescending(t => t.Version).ToList();
    }

    private IEnumerable<RhinoTarget> FromRegistry()
    {
        foreach (var keyName in _registry.GetSubKeyNames(RegistryRoot.LocalMachine, RhinoKey, RegistryViewKind.Registry64))
        {
            if (!MajorKeyRegex().IsMatch(keyName))
                continue;

            var installKey = $@"{RhinoKey}\{keyName}\Install";
            var installPath = _registry.GetString(RegistryRoot.LocalMachine, installKey, "InstallPath", RegistryViewKind.Registry64);
            if (string.IsNullOrWhiteSpace(installPath))
                continue;

            var versionText = _registry.GetString(RegistryRoot.LocalMachine, installKey, "Version", RegistryViewKind.Registry64);
            if (Build(installPath, versionText, TargetSource.Registry) is { } target)
                yield return target;
        }
    }

    private IEnumerable<RhinoTarget> FromProgramFiles()
    {
        foreach (var pf in _paths.ProgramFilesDirectories)
        {
            foreach (var dir in _fs.EnumerateDirectories(pf))
            {
                if (!Path.GetFileName(dir).StartsWith("Rhino ", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Build(dir, null, TargetSource.ProgramFilesScan) is { } target)
                    yield return target;
            }
        }
    }

    private RhinoTarget? Build(string installPath, string? versionText, TargetSource source)
    {
        var exe = Path.Combine(installPath, "System", "Rhino.exe");
        if (!_fs.FileExists(exe))
            return null;

        var version = (Version.TryParse(versionText?.Trim(), out var v) ? v : null) ?? _fs.GetFileVersion(exe);
        if (version is null || version.Major < MinimumMajor)
            return null;

        var profileKey = string.Create(CultureInfo.InvariantCulture, $"{version.Major}.0");

        // FR-81g: сведения о версии Rhino.exe → имя папки → BuildType из реестра.
        var strings = _fs.GetVersionStrings(exe);
        var buildType = _registry.GetString(RegistryRoot.LocalMachine, $@"{RhinoKey}\{profileKey}\Install", "BuildType", RegistryViewKind.Registry64);
        var evidence = new ReleaseEvidence(strings?.FileDescription, strings?.SpecialBuild, strings?.ProductName,
            Path.GetFileName(NormalizeDir(installPath)), buildType);
        var release = _releaseRules.Detect(evidence);
        var yak = Path.Combine(installPath, "System", "Yak.exe");

        return new RhinoTarget
        {
            Version = version,
            InstallPath = installPath,
            RhinoExePath = exe,
            ReleaseType = release.Type,
            ReleaseEvidence = evidence,
            ReleaseTypeMatch = release,
            UserPackagesDirectory = Path.Combine(_paths.RhinoUserPackagesRoot, profileKey),
            AllUsersPackagesDirectory = Path.Combine(_paths.RhinoProgramDataRoot, profileKey, "Packages"),
            GrasshopperLibrariesDirectory = _paths.GrasshopperLibraries,
            BuiltInYakPath = _fs.FileExists(yak) ? yak : null,
            Runtime = _runtimes.Resolve(RhinoRelease.FromVersion(version)),
            Source = source,
        };
    }

    private static string NormalizeDir(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    [GeneratedRegex(@"^\d+\.\d+$")]
    private static partial Regex MajorKeyRegex();
}
