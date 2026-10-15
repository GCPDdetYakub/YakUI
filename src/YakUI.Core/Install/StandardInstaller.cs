using YakUI.Core.Cli;
using YakUI.Core.Compat;
using YakUI.Core.Logging;
using YakUI.Core.Platform;
using YakUI.Core.Targets;
using YakUI.Core.Tools;
using YakUI.Core.Localization;

namespace YakUI.Core.Install;

/// <summary>Что ставить: скачанный файл выбранного дистрибутива (AD-03: дистрибутив выбирает YakUI по 17.6).</summary>
public sealed record InstallRequest(
    RhinoTarget Target,
    string PackageName,
    string Version,
    string YakFile,
    DistributionTag Tag,
    string? Sha256 = null,
    bool ForceBuiltIn = false);

public sealed record InstallResult(
    InstallMethod Method,
    string Name,
    string Version,
    string VersionDirectory,
    bool VerifiedByYakList,
    bool PendingRestart,
    string? YakFailure,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Стандартная установка в профиль текущего пользователя (AD-03, FR-40, FR-50):
/// <list type="number">
/// <item>проверки: Mac-only и несовместимые дистрибутивы не ставятся (FR-23, раздел 17);</item>
/// <item>yak цели — только привязанный к ней (FR-19a) — выполняет <c>yak install "&lt;файл.yak&gt;"</c>;</item>
/// <item>если yak нет, он отказал или <c>yak list</c> не показал пакет — встроенный установщик (FR-50);</item>
/// <item>проверка через <c>yak list</c> yak-а цели; без yak — по профилю, с пометкой «не подтверждено yak».</item>
/// </list>
/// Каждая установка — в журнал; встроенная — ещё и в реестр установок (FR-46, для удаления без yak, FR-48).
/// «Для всех пользователей» не поддерживается (FR-40).
/// </summary>
public sealed class StandardInstaller
{
    private readonly YakToolLibrary _library;
    private readonly YakCli _cli;
    private readonly ProfileScanner _scanner;
    private readonly InstallRegistry _registry;
    private readonly IRhinoProcessProbe _processes;
    private readonly OperationJournal? _journal;
    private readonly IAppLog? _log;
    private readonly TimeProvider _time;

    public StandardInstaller(
        YakToolLibrary library,
        YakCli cli,
        ProfileScanner scanner,
        InstallRegistry registry,
        IRhinoProcessProbe processes,
        OperationJournal? journal = null,
        IAppLog? log = null,
        TimeProvider? time = null)
    {
        _library = library;
        _cli = cli;
        _scanner = scanner;
        _registry = registry;
        _processes = processes;
        _journal = journal;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public async Task<InstallResult> InstallAsync(InstallRequest request, CancellationToken ct = default)
    {
        var target = request.Target;
        var warnings = new List<string>();

        // FR-23, раздел 17: Mac-only и несовместимое не ставится; совместимое снизу — с предупреждением.
        var compat = CompatibilityRules.Evaluate(request.Tag, target.Release);
        if (compat.PlatformMismatch)
            throw new InstallFailedException(L.T("StandardInstaller.100", $"Дистрибутив {request.Tag} — только для Mac."));
        if (compat.Status == CompatibilityStatus.Incompatible)
            throw new InstallFailedException(L.T("StandardInstaller.99", $"Дистрибутив {request.Tag} несовместим с {target.DisplayName}."));
        if (compat.Status == CompatibilityStatus.BackwardCompatible)
            warnings.Add(L.T("StandardInstaller.98", $"Дистрибутив {request.Tag} собран под более старую версию Rhino; работа не гарантируется."));
        if (!File.Exists(request.YakFile))
            throw new InstallFailedException(L.T("StandardInstaller.97", $"Файл не найден: {request.YakFile}"));

        var running = _processes.FindRunning(target.InstallPath).Count > 0;

        // 1. yak цели.
        string? yakFailure = null;
        ProfileState? before = null;
        var yak = _library.SelectFor(target.Major);
        if (request.ForceBuiltIn)
        {
            yakFailure = L.T("StandardInstaller.96", $"встроенный установщик выбран явно");
        }
        else if (yak is null)
        {
            yakFailure = L.T("StandardInstaller.95", $"нет пригодного yak, привязанного к Rhino {target.Major}");
        }
        else
        {
            YakToolLibrary.EnsureBinding(yak, target); // FR-19a
            before = ProfileState.Capture(target.UserPackagesDirectory, request.PackageName, request.Version);
            var r = await _cli.InstallFileAsync(yak.Path, request.YakFile, ct).ConfigureAwait(false);
            if (r.TimedOut || r.ExitCode != 0)
            {
                yakFailure = L.T("StandardInstaller.94", $"yak install: код {r.ExitCode}. {Short(r.StdErr.Length > 0 ? r.StdErr : r.StdOut)}");
            }
            else if (await VerifyWithYakAsync(yak, target, request.PackageName, request.Version, ct).ConfigureAwait(false) is { } dir)
            {
                Journal(request, InstallMethod.Yak, dir, null);
                return new InstallResult(InstallMethod.Yak, request.PackageName, request.Version, dir, true, running, null, warnings);
            }
            else
            {
                yakFailure = L.T("StandardInstaller.93", $"yak install завершился успешно, но yak list не показал эту версию");
            }
        }

        // 2. Встроенный установщик (FR-50). Если yak запускался, его остатки этой версии этого пакета
        //    убираются; папку, существовавшую до yak, встроенный установщик поверх не пишет.
        if (before is not null)
            CleanUpAfterYak(before, target, request);
        _log?.Info($"Установка {request.PackageName} {request.Version} в {target.DisplayName}: встроенный установщик ({yakFailure}).");
        var builtIn = BuiltInInstaller.Install(request.YakFile, target.UserPackagesDirectory, expectedName: request.PackageName, expectedVersion: request.Version);
        _registry.Add(new InstallRecord(0, TargetKey(target), builtIn.Name, builtIn.Version, builtIn.VersionDirectory, InstallMethod.BuiltIn, request.Sha256, _time.GetUtcNow()));

        // 3. Проверка: yak list цели, иначе — профиль.
        var verified = yak is not null && await VerifyWithYakAsync(yak, target, builtIn.Name, builtIn.Version, ct).ConfigureAwait(false) is not null;
        if (!verified)
        {
            var scanned = _scanner.Scan(target.UserPackagesDirectory).FirstOrDefault(p => p.Name.Equals(builtIn.Name, StringComparison.OrdinalIgnoreCase));
            if (scanned?.ActiveVersion is null || !BuiltInInstaller.SameVersion(scanned.ActiveVersion, builtIn.Version))
                throw new InstallFailedException(L.T("StandardInstaller.92", $"После установки пакет не виден в профиле."));
            warnings.Add(yak is null
                ? L.T("StandardInstaller.91", $"Установка не подтверждена yak: пригодного yak для этого Rhino нет; проверено по профилю.")
                : L.T("StandardInstaller.90", $"yak list не показал пакет после встроенной установки; проверено по профилю."));
        }

        Journal(request, InstallMethod.BuiltIn, builtIn.VersionDirectory, yakFailure);
        return new InstallResult(InstallMethod.BuiltIn, builtIn.Name, builtIn.Version, builtIn.VersionDirectory, verified, running, yakFailure, warnings);
    }

    private void CleanUpAfterYak(ProfileState before, RhinoTarget target, InstallRequest request)
    {
        if (before.VersionDirExisted)
            throw new InstallFailedException(
                L.T("StandardInstaller.89", $"yak не смог установить {request.PackageName} {request.Version}, а папка этой версии уже была в профиле до установки ") +
                L.T("StandardInstaller.88", $"({before.VersionDir}); её состояние после отказа yak неизвестно. Встроенный установщик не запускался — проверьте или удалите папку и повторите."));

        var removed = new List<string>();
        if (Directory.Exists(before.VersionDir))
        {
            Directory.Delete(before.VersionDir, recursive: true);
            removed.Add(before.VersionDir);
        }

        if (before.ManifestTxt is { } text)
            File.WriteAllText(before.ManifestPath, text);
        else if (File.Exists(before.ManifestPath))
        {
            File.Delete(before.ManifestPath);
            removed.Add(before.ManifestPath);
        }

        if (!before.PackageDirExisted && Directory.Exists(before.PackageDir) && !Directory.EnumerateFileSystemEntries(before.PackageDir).Any())
        {
            Directory.Delete(before.PackageDir);
            removed.Add(before.PackageDir);
        }

        if (removed.Count > 0)
        {
            _log?.Warning($"Убраны остатки yak перед встроенной установкой: {string.Join("; ", removed)}");
            _journal?.Write("install", $"{request.PackageName} {request.Version} → Rhino {target.Major}", "yak-leftovers-removed", details: new { removed });
        }
    }

    /// <summary>Состояние папки пакета до вызова yak: только эта версия этого пакета и его manifest.txt.</summary>
    private sealed record ProfileState(string PackageDir, string VersionDir, string ManifestPath, bool PackageDirExisted, bool VersionDirExisted, string? ManifestTxt)
    {
        public static ProfileState Capture(string profile, string name, string version)
        {
            var packageDir = Path.Combine(profile, name);
            var versionDir = Path.Combine(packageDir, version);
            var manifest = Path.Combine(packageDir, "manifest.txt");
            return new ProfileState(packageDir, versionDir, manifest, Directory.Exists(packageDir), Directory.Exists(versionDir),
                File.Exists(manifest) ? File.ReadAllText(manifest) : null);
        }
    }

    /// <summary>Папка версии, если <c>yak list</c> yak-а цели показывает пакет с этой версией; иначе null.</summary>
    private async Task<string?> VerifyWithYakAsync(YakTool yak, RhinoTarget target, string name, string version, CancellationToken ct)
    {
        try
        {
            var list = await _library.ListInstalledAsync(yak, target, ct).ConfigureAwait(false);
            var entry = list.Packages.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry is null || !BuiltInInstaller.SameVersion(entry.Version, version))
                return null;
            return Path.Combine(target.UserPackagesDirectory, entry.Name, entry.Version);
        }
        catch (Exception e) when (e is YakCliException or YakBindingException)
        {
            _log?.Warning($"Проверка через yak list не удалась: {e.Message}");
            return null;
        }
    }

    private void Journal(InstallRequest request, InstallMethod method, string dir, string? yakFailure) =>
        _journal?.Write("install", $"{request.PackageName} {request.Version} → Rhino {request.Target.Major}", method == InstallMethod.Yak ? "yak" : "builtin",
            details: new { request.Target.InstallPath, dir, file = request.YakFile, tag = request.Tag.Original, yakFailure });

    public static string TargetKey(RhinoTarget target) => TargetChoice.Installed(target).Key;

    private static string Short(string s)
    {
        s = s.Trim().Replace("\r", "", StringComparison.Ordinal);
        return s.Length > 300 ? s[..300] + "…" : s;
    }
}
