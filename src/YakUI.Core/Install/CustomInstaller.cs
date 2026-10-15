using YakUI.Core.Logging;
using YakUI.Core.Platform;
using YakUI.Core.Targets;
using YakUI.Core.Localization;

namespace YakUI.Core.Install;

/// <summary>Что ставить по пользовательскому пути: файл <c>.yak</c> или уже собранная папка версии (импорт, FR-101).</summary>
public sealed record CustomInstallRequest(
    RhinoTarget Target,
    string Root,
    string PackageName,
    string Version,
    InstallMode Mode,
    string? YakFile = null,
    string? PreparedDirectory = null,
    string? Sha256 = null,
    InstallSource Source = InstallSource.Catalog,
    string? SourceFile = null,
    YakUI.Core.Compat.DotNetRuntimeKind? Runtime = null);

public sealed record CustomInstallResult(
    string Name,
    string Version,
    string VersionDirectory,
    InstallMode Mode,
    string? GhLink,
    IReadOnlyList<string> RhinoPlugins,
    IReadOnlyList<string> UserObjects,
    IReadOnlyList<string> Warnings);

public sealed record CustomUninstallResult(string Name, IReadOnlyList<string> Versions, IReadOnlyList<string> RemovedLinks);

/// <summary>
/// Пользовательский путь (7.1, FR-40…FR-47): только встроенный установщик, раскладка <c>&lt;Root&gt;/&lt;Name&gt;/&lt;Version&gt;/</c>,
/// содержимое как есть (FR-42). Регистрация: Grasshopper — <c>.ghlink</c> на папку или подпапку среды цели (FR-43, 21.6);
/// <c>.rhp</c> — только распаковка и инструкция (FR-44, регистрация — 1.1); <c>.ghuser</c> — копии в UserObjects.
/// «Только распаковать» — без регистрации (FR-45). Всё созданное записывается в реестр (FR-46) и удаляется только оно.
/// С распакованных <c>.gha</c>/<c>.rhp</c>/<c>.dll</c> снимается Mark-of-the-Web (FR-103).
/// </summary>
public sealed class CustomInstaller
{
    private readonly InstallRegistry _registry;
    private readonly IRhinoProcessProbe _processes;
    private readonly IMarkOfTheWeb _motw;
    private readonly PendingRemovals? _pending;
    private readonly OperationJournal? _journal;
    private readonly TimeProvider _time;
    private readonly Func<IEnumerable<string>>? _rhinoInstallPaths;

    public CustomInstaller(InstallRegistry registry, IRhinoProcessProbe processes, IMarkOfTheWeb motw, PendingRemovals? pending = null,
        OperationJournal? journal = null, TimeProvider? time = null, Func<IEnumerable<string>>? rhinoInstallPaths = null)
    {
        _rhinoInstallPaths = rhinoInstallPaths;
        _registry = registry;
        _processes = processes;
        _motw = motw;
        _pending = pending;
        _journal = journal;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Какой-либо Rhino может держать файлы: ссылка .ghlink общая для всех версий Rhino (21.6), поэтому проверяются все
    /// установленные; Windows даёт переименовать папку с загруженной сборкой, но не удалить файл.
    /// </summary>
    private string? RunningRhino(RhinoTarget target)
    {
        foreach (var path in (_rhinoInstallPaths?.Invoke() ?? []).Append(target.InstallPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_processes.FindRunning(path).Count > 0)
                return path;
        }

        return null;
    }

    /// <summary>Среда для выбора подпапки: явно выбранная пользователем или среда цели по 17a.</summary>
    public static YakUI.Core.Compat.TargetRuntime RuntimeFor(RhinoTarget target, YakUI.Core.Compat.DotNetRuntimeKind? chosen) => chosen switch
    {
        YakUI.Core.Compat.DotNetRuntimeKind.NetFramework when target.Runtime.Runtime != YakUI.Core.Compat.DotNetRuntimeKind.NetFramework =>
            new YakUI.Core.Compat.TargetRuntime(YakUI.Core.Compat.DotNetRuntimeKind.NetFramework, "4.8", false, true),
        _ => target.Runtime,
    };

    /// <summary>Папка пользовательских объектов Grasshopper рядом с Libraries.</summary>
    public static string UserObjectsDirectory(RhinoTarget t) =>
        Path.Combine(Path.GetDirectoryName(t.GrasshopperLibrariesDirectory.TrimEnd('\\', '/'))!, "UserObjects");

    public CustomInstallResult Install(CustomInstallRequest r)
    {
        if (r.Mode == InstallMode.Standard)
            throw new ArgumentException(L.T("CustomInstaller.69", $"Стандартный режим — StandardInstaller."), nameof(r));
        Directory.CreateDirectory(r.Root);

        // 1. Папка версии (атомарно; та же версия заменяется, другие не трогаются — BuiltInInstaller).
        BuiltInResult placed;
        if (r.YakFile is not null)
        {
            placed = BuiltInInstaller.Install(r.YakFile, r.Root, expectedName: r.PackageName, expectedVersion: r.Version);
        }
        else
        {
            placed = PlaceDirectory(r.PreparedDirectory ?? throw new ArgumentException(L.T("CustomInstaller.68", $"Нет ни файла .yak, ни папки."), nameof(r)), r.Root, r.PackageName, r.Version);
        }

        var dir = placed.VersionDirectory;
        var warnings = new List<string>();
        var links = new List<string>();
        string? ghlink = null;

        // 2. Mark-of-the-Web со сборок (FR-103).
        var unblocked = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                     .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".gha" or ".ghpy" or ".rhp" or ".dll"))
        {
            if (_motw.Has(f))
            {
                _motw.Remove(f);
                unblocked.Add(f);
            }
        }

        if (unblocked.Count > 0)
            _journal?.Write("unblock", $"{placed.Name} {placed.Version}", "ok", details: unblocked);

        var rhp = Directory.EnumerateFiles(dir, "*.rhp", SearchOption.AllDirectories).ToList();
        var userObjects = new List<string>();

        if (r.Mode == InstallMode.Custom)
        {
            // 3. Grasshopper: ссылка на папку или подпапку среды цели (FR-43, 21.6).
            if (GhLinks.HasAnyGh(dir))
            {
                // Среда, выбранная пользователем (Rhino.Inside в Revit 2021–2024 и Rhino 8 в режиме NetFx — .NET Framework),
                // иначе — среда цели по 17a (FR-43, 21.6).
                var runtime = RuntimeFor(r.Target, r.Runtime);
                if (GhLinks.LinkFolder(dir, runtime) is { } folder)
                {
                    // Ссылка общая для всех версий Rhino (21.6): «своя» — если её создал YakUI для любой цели.
                    var linkPath = GhLinks.LinkPath(r.Target.GrasshopperLibrariesDirectory, placed.Name);
                    var ours = _registry.Custom().Any(x => x.Links.Contains(linkPath, StringComparer.OrdinalIgnoreCase));
                    ghlink = GhLinks.Write(r.Target.GrasshopperLibrariesDirectory, placed.Name, folder, ours);
                    links.Add(ghlink);
                }
                else
                {
                    warnings.Add(L.T("CustomInstaller.67", $"Нет сборки Grasshopper под среду {runtime.DisplayName} — ссылка .ghlink не создана."));
                }
            }

            // 4. Пользовательские объекты — копии в UserObjects\<Имя>\.
            var ghuser = Directory.EnumerateFiles(dir, "*.ghuser", SearchOption.AllDirectories).ToList();
            if (ghuser.Count > 0)
            {
                var uoDir = Path.Combine(UserObjectsDirectory(r.Target), placed.Name);
                Directory.CreateDirectory(uoDir);
                // Чужие файлы с тем же именем не перезаписываются (не из реестра YakUI) — предупреждение (FR-42, 13a).
                var ours = _registry.Custom().SelectMany(x => x.Links).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var skipped = new List<string>();
                foreach (var f in ghuser)
                {
                    var dest = Path.Combine(uoDir, Path.GetFileName(f));
                    if (File.Exists(dest) && !ours.Contains(dest))
                    {
                        skipped.Add(Path.GetFileName(dest));
                        continue;
                    }

                    File.Copy(f, dest, overwrite: true);
                    userObjects.Add(dest);
                }

                if (skipped.Count > 0)
                    warnings.Add(L.T("CustomInstaller.66", $"В {uoDir} уже есть файлы, созданные не YakUI, — не перезаписаны: {string.Join(", ", skipped)}."));

                links.AddRange(userObjects);
            }

            if (rhp.Count > 0)
                warnings.Add(L.T("CustomInstaller.65", $"Rhino-плагины (.rhp) распакованы, но не зарегистрированы (FR-44): перетащите .rhp в окно Rhino или PluginManager → Install."));
        }

        // 5. Реестр (FR-46); прежние записи этой версии заменяются.
        _registry.Add(new InstallRecord(0, StandardInstaller.TargetKey(r.Target), placed.Name, placed.Version, dir, InstallMethod.BuiltIn, r.Sha256, _time.GetUtcNow())
        {
            Mode = r.Mode,
            Root = r.Root,
            Links = links,
            Source = r.Source,
            SourceFile = r.SourceFile,
        });
        _journal?.Write("install", $"{placed.Name} {placed.Version} → {r.Root} (Rhino {r.Target.Major})", r.Mode == InstallMode.Unpack ? "unpack" : "custom",
            details: new { dir, links, rhp, r.Source, r.SourceFile, warnings });
        return new CustomInstallResult(placed.Name, placed.Version, dir, r.Mode, ghlink, rhp, userObjects, warnings);
    }

    /// <summary>
    /// Смена версии по пользовательскому пути (FR-49): новая версия ставится рядом, ссылка переводится на неё, прежняя папка
    /// удаляется (при открытом Rhino — отложенно). При ошибке установки ссылка и прежняя версия остаются.
    /// </summary>
    public CustomInstallResult ChangeVersion(InstallRecord current, CustomInstallRequest next)
    {
        var result = Install(next);
        if (BuiltInInstaller.SameVersion(current.Version, result.Version))
            return result;

        var warnings = new List<string>(result.Warnings);
        // Ссылки прежней версии, которые не переписаны новой (например, UserObjects другого состава).
        foreach (var link in current.Links.Where(l => !result.UserObjects.Contains(l, StringComparer.OrdinalIgnoreCase) && !string.Equals(l, result.GhLink, StringComparison.OrdinalIgnoreCase)))
            TryDeleteFile(link);

        // Папка прежней версии нужна другой версии Rhino — не трогается (общая папка версии, 21.6).
        var sharedOld = _registry.Custom().Any(o => o.Id != current.Id && string.Equals(o.Path, current.Path, StringComparison.OrdinalIgnoreCase));
        if (sharedOld)
        {
            warnings.Add(L.T("CustomInstaller.64", $"Папка версии {current.Version} используется для другой версии Rhino — оставлена."));
        }
        else if (RunningRhino(next.Target) is { } runningPath && _pending is not null)
        {
            _pending.Add(current.Path, runningPath, current.Name, current.Version);
            warnings.Add(L.T("CustomInstaller.63", $"Rhino запущен — папка прежней версии {current.Version} будет удалена автоматически после закрытия Rhino."));
        }
        else if (!TryRemoveDirectory(current.Path, current.Root ?? next.Root))
        {
            warnings.Add(L.T("CustomInstaller.62", $"Папку прежней версии {current.Version} удалить не удалось; она оставлена."));
        }

        _registry.Remove(current.Id);
        return result with { Warnings = warnings };
    }

    /// <summary>Удаление по реестру (FR-46): папки версий, ссылки и объекты, созданные YakUI; ничего чужого.</summary>
    public CustomUninstallResult Uninstall(RhinoTarget target, string name, string? root = null)
    {
        var records = _registry.Custom()
            .Where(r => r.TargetKey == StandardInstaller.TargetKey(target) && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && (root is null || string.Equals(r.Root, root, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (records.Count == 0)
            throw new InstallFailedException(L.T("CustomInstaller.61", $"YakUI не ставил {name} по пользовательскому пути для {target.DisplayName}."));

        // Папки и ссылки, которые числятся и за другой версией Rhino (общая папка версии, общая .ghlink), не трогаются:
        // снимается только запись этой цели.
        var others = _registry.Custom().Where(o => !records.Any(r => r.Id == o.Id)).ToList();
        bool SharedDir(string path) => others.Any(o => string.Equals(o.Path, path, StringComparison.OrdinalIgnoreCase));
        bool SharedLink(string link) => others.Any(o => o.Links.Contains(link, StringComparer.OrdinalIgnoreCase));

        // Запущенный Rhino может держать загруженные сборки — тогда удаление прошло бы частично (FR-48): отказ, ничего не удалено.
        // «Только распаковать» Rhino не загружает — проверка не нужна.
        if (records.Any(r => r.Mode == InstallMode.Custom) && RunningRhino(target) is { } held)
            throw new InstallFailedException(L.T("CustomInstaller.60", $"Не удалось удалить {name}: запущен Rhino ({held}) и может держать файлы пакета. Закройте Rhino и повторите. Ничего не удалено."));

        // Сначала папки (атомарно; занятые файлы — ошибка, ничего не удалено), потом ссылки.
        var running = false;
        var moved = new List<(string From, string Temp)>();
        try
        {
            foreach (var r in records.Where(r => Directory.Exists(r.Path) && !SharedDir(r.Path)))
            {
                var temp = Path.Combine(r.Root ?? Path.GetDirectoryName(Path.GetDirectoryName(r.Path)!)!, StandardUninstaller.DeletePrefix + Guid.NewGuid().ToString("N"));
                Directory.Move(r.Path, temp);
                moved.Add((r.Path, temp));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            foreach (var (from, temp) in moved)
                Directory.Move(temp, from);
            throw new InstallFailedException(L.T("CustomInstaller.59", $"Не удалось удалить {name}: файлы заняты ({e.Message}). {(running ? L.T("CustomInstaller.58", $"Закройте Rhino и повторите. ") : "")}Ничего не удалено."), e);
        }

        foreach (var (_, temp) in moved)
            BuiltInInstaller.DeleteQuietly(temp);

        var removedLinks = new List<string>();
        foreach (var r in records)
        {
            var packageDir = Path.GetDirectoryName(r.Path)!;
            foreach (var link in r.Links.Where(l => !SharedLink(l)))
            {
                var removed = link.EndsWith(GhLinks.Extension, StringComparison.OrdinalIgnoreCase)
                    ? GhLinks.Remove(link, packageDir)
                    : TryDeleteFile(link);
                if (removed)
                    removedLinks.Add(link);
            }

            // Пустая папка UserObjects\<Имя> и manifest.txt с пустой папкой пакета.
            foreach (var uoDir in r.Links.Where(l => !SharedLink(l) && !l.EndsWith(GhLinks.Extension, StringComparison.OrdinalIgnoreCase)).Select(Path.GetDirectoryName).OfType<string>().Distinct())
                TryDeleteEmpty(uoDir);
            if (Directory.Exists(packageDir) && !Directory.EnumerateDirectories(packageDir).Any())
            {
                TryDeleteFile(Path.Combine(packageDir, "manifest.txt"));
                TryDeleteEmpty(packageDir);
            }

            _registry.Remove(r.Id);
        }

        _journal?.Write("uninstall", $"{name} → {records[0].Root} (Rhino {target.Major})", "custom",
            details: new { versions = records.Select(r => r.Version), removedLinks });
        return new CustomUninstallResult(name, records.Select(r => r.Version).ToList(), removedLinks);
    }

    /// <summary>
    /// FR-47: пакет стоит и в стандартном профиле цели, и по пользовательскому пути — возможна двойная загрузка.
    /// </summary>
    public IReadOnlyList<string> Duplicates(RhinoTarget target, IEnumerable<string> standardPackages)
    {
        var custom = _registry.Custom().Where(r => r.TargetKey == StandardInstaller.TargetKey(target) && r.Mode == InstallMode.Custom)
            .Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return standardPackages.Where(custom.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Готовая папка (импорт) → <c>&lt;Root&gt;/&lt;Name&gt;/&lt;Version&gt;</c> атомарно, с manifest.txt.</summary>
    private static BuiltInResult PlaceDirectory(string source, string root, string name, string version)
    {
        var packageDir = Path.Combine(root, name);
        var versionDir = Path.Combine(packageDir, version);
        Directory.CreateDirectory(packageDir);
        var replaced = Directory.Exists(versionDir);
        string? backup = null;
        if (replaced)
        {
            backup = Path.Combine(root, ".yakui-tmp-old-" + Guid.NewGuid().ToString("N"));
            Directory.Move(versionDir, backup);
        }

        try
        {
            Directory.Move(source, versionDir);
        }
        catch
        {
            if (backup is not null)
                Directory.Move(backup, versionDir);
            throw;
        }

        if (backup is not null)
            BuiltInInstaller.DeleteQuietly(backup);
        File.WriteAllText(Path.Combine(packageDir, "manifest.txt"), version + "\n");
        return new BuiltInResult(name, version, versionDir, replaced);
    }

    private static bool TryRemoveDirectory(string dir, string root)
    {
        if (!Directory.Exists(dir))
            return true;
        var temp = Path.Combine(root, StandardUninstaller.DeletePrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.Move(dir, temp);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        BuiltInInstaller.DeleteQuietly(temp);
        return true;
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
