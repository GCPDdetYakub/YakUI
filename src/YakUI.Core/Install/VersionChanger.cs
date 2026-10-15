using System.Text;
using YakUI.Core.Logging;
using YakUI.Core.Platform;
using YakUI.Core.Targets;
using YakUI.Core.Localization;

namespace YakUI.Core.Install;

public sealed record VersionChangeResult(
    string Name,
    string? OldVersion,
    string NewVersion,
    InstallMethod Method,
    bool OldVersionRemoved,
    bool PendingRestart,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Смена версии пакета — обновление или откат (FR-49): «установить новую, затем снять старую».
/// <list type="number">
/// <item>новая ставится стандартной установкой (yak цели, иначе встроенный установщик) и проверяется;</item>
/// <item>при ошибке активной остаётся прежняя версия: <c>manifest.txt</c> возвращается, папка прежней версии не трогается;</item>
/// <item>yak прежнюю папку не удаляет (21.5, ОВ-04) — её снимает YakUI: атомарно (переименование → удаление) и только при
/// закрытом Rhino цели; не вышло — папка остаётся неактивной, с предупреждением.</item>
/// </list>
/// Другие неактивные версии не трогаются.
/// </summary>
public sealed class VersionChanger
{
    private readonly StandardInstaller _installer;
    private readonly ProfileScanner _scanner;
    private readonly InstallRegistry _registry;
    private readonly IRhinoProcessProbe _processes;
    private readonly OperationJournal? _journal;
    private readonly IAppLog? _log;
    private readonly PendingRemovals? _pending;

    public VersionChanger(StandardInstaller installer, ProfileScanner scanner, InstallRegistry registry, IRhinoProcessProbe processes,
        OperationJournal? journal = null, IAppLog? log = null, PendingRemovals? pending = null)
    {
        _pending = pending;
        _installer = installer;
        _scanner = scanner;
        _registry = registry;
        _processes = processes;
        _journal = journal;
        _log = log;
    }

    public async Task<VersionChangeResult> ChangeAsync(InstallRequest request, CancellationToken ct = default)
    {
        var target = request.Target;
        var before = Find(target, request.PackageName);
        var oldVersion = before?.ActiveVersion;
        if (oldVersion is not null && BuiltInInstaller.SameVersion(oldVersion, request.Version))
            throw new InstallFailedException(L.T("VersionChanger.120", $"{request.PackageName} {request.Version} уже активна в {target.DisplayName}."));

        var manifestPath = Path.Combine(target.UserPackagesDirectory, before?.Name ?? request.PackageName, "manifest.txt");
        var manifestBefore = File.Exists(manifestPath) ? File.ReadAllText(manifestPath) : null;

        InstallResult installed;
        try
        {
            installed = await _installer.InstallAsync(request, ct).ConfigureAwait(false);
        }
        catch
        {
            RestoreManifest(manifestPath, manifestBefore, oldVersion);
            Journal(request, oldVersion, "failed", null);
            throw;
        }

        var warnings = new List<string>(installed.Warnings);
        var after = Find(target, request.PackageName);
        if (after?.ActiveVersion is null || !BuiltInInstaller.SameVersion(after.ActiveVersion, request.Version))
        {
            RestoreManifest(manifestPath, manifestBefore, oldVersion);
            Journal(request, oldVersion, "not-active", null);
            throw new InstallFailedException(L.T("VersionChanger.119", $"После установки активна версия {after?.ActiveVersion ?? "—"}, а не {request.Version}; оставлена {oldVersion ?? L.T("VersionChanger.118", $"прежняя")}."));
        }

        var removed = false;
        if (oldVersion is not null && before is not null)
        {
            var oldDir = Path.Combine(before.Directory, oldVersion);
            if (installed.PendingRestart || _processes.FindRunning(target.InstallPath).Count > 0)
            {
                // FR-49 (ТЗ 0.3.5): Rhino открыт — удаление откладывается и выполняется само, когда Rhino закрыт.
                if (_pending is not null && Directory.Exists(oldDir))
                {
                    _pending.Add(oldDir, target.InstallPath, request.PackageName, oldVersion);
                    warnings.Add(L.T("VersionChanger.117", $"Rhino запущен — папка прежней версии {oldVersion} будет удалена автоматически после закрытия Rhino."));
                }
                else
                {
                    warnings.Add(L.T("VersionChanger.116", $"Rhino запущен — папка прежней версии {oldVersion} оставлена неактивной."));
                }
            }
            else if (Directory.Exists(oldDir))
            {
                removed = TryRemove(oldDir, target, out var why);
                if (removed)
                {
                    foreach (var rec in _registry.ForPackage(StandardInstaller.TargetKey(target), request.PackageName)
                                 .Where(r => BuiltInInstaller.SameVersion(r.Version, oldVersion)))
                        _registry.Remove(rec.Id);
                }
                else
                {
                    warnings.Add(L.T("VersionChanger.115", $"Папку прежней версии {oldVersion} удалить не удалось ({why}); она оставлена неактивной."));
                }
            }
        }

        Journal(request, oldVersion, "ok", new { method = installed.Method.ToString(), oldRemoved = removed, warnings });
        return new VersionChangeResult(installed.Name, oldVersion, installed.Version, installed.Method, removed, installed.PendingRestart, warnings);
    }

    private bool TryRemove(string dir, RhinoTarget target, out string? why)
    {
        var temp = Path.Combine(target.UserPackagesDirectory, StandardUninstaller.DeletePrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.Move(dir, temp);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            why = e.Message;
            return false;
        }

        BuiltInInstaller.DeleteQuietly(temp);
        why = null;
        return true;
    }

    /// <summary>Вернуть активной прежнюю версию, если установка успела переключить <c>manifest.txt</c>.</summary>
    private void RestoreManifest(string path, string? before, string? oldVersion)
    {
        try
        {
            var now = File.Exists(path) ? File.ReadAllText(path) : null;
            if (now == before)
                return;
            if (before is null)
            {
                if (oldVersion is null && File.Exists(path) && !Directory.EnumerateDirectories(Path.GetDirectoryName(path)!).Any())
                    File.Delete(path);
                return;
            }

            File.WriteAllText(path + ".tmp", before, new UTF8Encoding(false));
            File.Move(path + ".tmp", path, overwrite: true);
            _log?.Warning($"Смена версии не удалась: manifest.txt возвращён к «{before.Trim()}».");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Error($"Не удалось вернуть manifest.txt ({path})", e);
        }
    }

    private ProfilePackage? Find(RhinoTarget target, string name) =>
        _scanner.Scan(target.UserPackagesDirectory).FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private void Journal(InstallRequest r, string? oldVersion, string result, object? extra) =>
        _journal?.Write("version-change", $"{r.PackageName} {oldVersion ?? "—"} → {r.Version} (Rhino {r.Target.Major})", result,
            details: new { r.Target.InstallPath, file = r.YakFile, extra });
}
