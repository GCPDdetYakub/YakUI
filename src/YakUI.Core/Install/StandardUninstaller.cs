using YakUI.Core.Cli;
using YakUI.Core.Logging;
using YakUI.Core.Platform;
using YakUI.Core.Targets;
using YakUI.Core.Tools;
using YakUI.Core.Localization;

namespace YakUI.Core.Install;

public sealed record UninstallResult(
    InstallMethod Method,
    string Name,
    IReadOnlyList<string> Versions,
    bool PendingRestart);

/// <summary>Что будет удалено (для подтверждения, FR-85): все версии пакета в профиле цели.</summary>
public sealed record UninstallPreview(string Name, IReadOnlyList<string> Versions, string? ActiveVersion, bool RhinoRunning, string? BlockedReason);

/// <summary>
/// Удаление пакета из стандартного профиля цели целиком, со всеми версиями (FR-48; отдельная версия — этап 4):
/// <list type="number">
/// <item>есть пригодный yak цели — <c>yak uninstall &lt;Имя&gt;</c> с проверкой привязки (FR-19a), затем проверка
/// по <c>yak list</c> и сканированию профиля; остатки — понятная ошибка;</item>
/// <item>yak нет — только пакеты, все версии которых поставил встроенный установщик YakUI (реестр, FR-46):
/// папка пакета атомарно переименовывается во временное имя и удаляется; занятые файлы — ошибка, ничего не удалено;
/// остальное блокируется (AD-10).</item>
/// </list>
/// Профиль «для всех» не трогается. Записи реестра пакета снимаются, удаление — в журнал.
/// </summary>
public sealed class StandardUninstaller
{
    public const string DeletePrefix = ".yakui-del-";

    private readonly YakToolLibrary _library;
    private readonly YakCli _cli;
    private readonly ProfileScanner _scanner;
    private readonly InstallRegistry _registry;
    private readonly IRhinoProcessProbe _processes;
    private readonly OperationJournal? _journal;
    private readonly IAppLog? _log;

    public StandardUninstaller(YakToolLibrary library, YakCli cli, ProfileScanner scanner, InstallRegistry registry,
        IRhinoProcessProbe processes, OperationJournal? journal = null, IAppLog? log = null)
    {
        _library = library;
        _cli = cli;
        _scanner = scanner;
        _registry = registry;
        _processes = processes;
        _journal = journal;
        _log = log;
    }

    public UninstallPreview Preview(RhinoTarget target, string name)
    {
        var package = Find(target, name);
        var running = _processes.FindRunning(target.InstallPath).Count > 0;
        if (package is null)
            return new UninstallPreview(name, [], null, running, L.T("StandardUninstaller.114", $"Пакет {name} не найден в профиле {target.DisplayName}."));
        var blocked = _library.SelectFor(target.Major) is null ? BlockedWithoutYak(target, package) : null;
        return new UninstallPreview(package.Name, package.VersionDirectories, package.ActiveVersion, running, blocked);
    }

    public async Task<UninstallResult> UninstallAsync(RhinoTarget target, string name, CancellationToken ct = default)
    {
        var package = Find(target, name) ?? throw new InstallFailedException(L.T("StandardUninstaller.113", $"Пакет {name} не найден в профиле {target.DisplayName}."));
        var running = _processes.FindRunning(target.InstallPath).Count > 0;
        var versions = package.VersionDirectories;
        var yak = _library.SelectFor(target.Major);

        InstallMethod method;
        if (yak is not null)
        {
            YakToolLibrary.EnsureBinding(yak, target); // FR-19a
            var r = await _cli.UninstallAsync(yak.Path, package.Name, ct).ConfigureAwait(false);
            var left = Find(target, package.Name);
            var inList = await InYakListAsync(yak, target, package.Name, ct).ConfigureAwait(false);
            if (r.TimedOut || r.ExitCode != 0 || left is not null || inList)
            {
                var what = new List<string>();
                if (left is not null)
                    what.Add(L.T("StandardUninstaller.112", $"в профиле осталось: {left.Directory} ({string.Join(", ", left.VersionDirectories)})"));
                if (inList)
                    what.Add(L.T("StandardUninstaller.111", $"yak list всё ещё показывает пакет"));
                var output = (r.StdErr.Length > 0 ? r.StdErr : r.StdOut).Trim();
                Journal(target, package.Name, versions, "yak-failed", new { r.ExitCode, output, what });
                throw new InstallFailedException(
                    L.T("StandardUninstaller.110", $"yak uninstall {package.Name}: код {r.ExitCode}{(r.TimedOut ? L.T("StandardUninstaller.109", $" (превышено время)") : "")}. ") +
                    (what.Count > 0 ? string.Join("; ", what) + ". " : "") +
                    (running ? L.T("StandardUninstaller.108", $"Rhino запущен — возможно, файлы заняты; закройте Rhino и повторите. ") : "") + output);
            }

            method = InstallMethod.Yak;
        }
        else
        {
            if (BlockedWithoutYak(target, package) is { } reason)
                throw new InstallFailedException(reason);
            DeleteAtomically(target, package, running);
            method = InstallMethod.BuiltIn;
        }

        foreach (var rec in _registry.ForPackage(StandardInstaller.TargetKey(target), package.Name))
            _registry.Remove(rec.Id);
        Journal(target, package.Name, versions, method == InstallMethod.Yak ? "yak" : "builtin", null);
        return new UninstallResult(method, package.Name, versions, running);
    }

    /// <summary>Без yak — только если каждую версию в профиле поставил встроенный установщик YakUI (FR-48, AD-10).</summary>
    private string? BlockedWithoutYak(RhinoTarget target, ProfilePackage package)
    {
        var ours = _registry.ForPackage(StandardInstaller.TargetKey(target), package.Name)
            .Where(r => r.Method == InstallMethod.BuiltIn)
            .Select(r => r.Version)
            .ToList();
        if (ours.Count == 0)
        {
            return L.T("StandardUninstaller.107", $"Пригодного yak для {target.DisplayName} нет, а пакет {package.Name} поставлен не YakUI — удалить его без yak нельзя. ") +
                   L.T("StandardUninstaller.106", $"Удалите его в Rhino (_PackageManager).");
        }

        var foreign = package.VersionDirectories.Where(v => !ours.Any(o => BuiltInInstaller.SameVersion(o, v))).ToList();
        return foreign.Count > 0
            ? L.T("StandardUninstaller.105", $"Пригодного yak для {target.DisplayName} нет, а версии {string.Join(", ", foreign)} пакета {package.Name} поставлены не YakUI — удаление заблокировано.")
            : null;
    }

    /// <summary>Переименование папки пакета во временное имя (атомарно; занятые файлы — ошибка без частичного удаления), затем удаление.</summary>
    private void DeleteAtomically(RhinoTarget target, ProfilePackage package, bool running)
    {
        // Windows разрешает переименовать папку с загруженной сборкой, но не удалить файл — при запущенном Rhino удаление
        // прошло бы частично (FR-48: «ничего не удаляется частично»), поэтому сначала проверка процессов.
        if (running)
            throw new InstallFailedException(L.T("StandardUninstaller.104", $"Не удалось удалить {package.Name}: {target.DisplayName} запущен и может держать файлы пакета. Закройте Rhino и повторите. Ничего не удалено."));

        var temp = Path.Combine(target.UserPackagesDirectory, DeletePrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.Move(package.Directory, temp);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InstallFailedException(
                L.T("StandardUninstaller.103", $"Не удалось удалить {package.Name}: файлы заняты ({e.Message}). ") +
                (running ? L.T("StandardUninstaller.102", $"Закройте Rhino и повторите. ") : "") + L.T("StandardUninstaller.101", $"Ничего не удалено."), e);
        }

        BuiltInInstaller.DeleteQuietly(temp);
        if (Directory.Exists(temp))
            _log?.Warning($"Временная папка {temp} не удалена полностью; будет убрана при следующей установке.");
    }

    private ProfilePackage? Find(RhinoTarget target, string name) =>
        _scanner.Scan(target.UserPackagesDirectory).FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private async Task<bool> InYakListAsync(YakTool yak, RhinoTarget target, string name, CancellationToken ct)
    {
        try
        {
            var list = await _library.ListInstalledAsync(yak, target, ct).ConfigureAwait(false);
            return list.Packages.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is YakCliException or YakBindingException)
        {
            _log?.Warning($"Проверка через yak list после удаления не удалась: {e.Message}");
            return false;
        }
    }

    private void Journal(RhinoTarget target, string name, IReadOnlyList<string> versions, string result, object? extra) =>
        _journal?.Write("uninstall", $"{name} → Rhino {target.Major}", result, details: new { target.InstallPath, versions, extra });
}
