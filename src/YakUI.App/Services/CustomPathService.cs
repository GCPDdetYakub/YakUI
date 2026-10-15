using System.Globalization;
using YakUI.App.Resources;
using YakUI.App.ViewModels;
using YakUI.Core.Api;
using YakUI.Core.Compat;
using YakUI.Core.Downloads;
using YakUI.Core.Install;
using YakUI.Core.Platform;
using YakUI.Core.Settings;
using YakUI.Core.Targets;

namespace YakUI.App.Services;

/// <summary>Зависимости службы пользовательского пути (в приложении — AppHost; в тестах — фейки).</summary>
public sealed record CustomPathDeps(
    CustomInstaller Installer,
    StandardInstaller Standard,
    InstallRegistry Registry,
    ProfileScanner Scanner,
    IPackageFiles Files,
    IFileSystem FileSystem,
    IMarkOfTheWeb Motw,
    IRhinoInstallerEngine RhinoInstaller,
    IDialogService Dialogs,
    AppSettings Settings,
    Action Save,
    Func<IReadOnlyList<RhinoTarget>> Targets,
    Func<InstallOptionsViewModel, Task<InstallChoice?>> ShowOptions,
    Func<Func<CancellationToken, Task>, string, Task> RunInBackground,
    Action Rescan,
    Func<string, bool> OpenFolder,
    Func<Task<string?>>? PickFolder = null);

/// <summary>
/// Пользовательский путь в приложении (этап 3): окно режима установки (FR-40, FR-45), установка из каталога в свою папку
/// (FR-42…FR-44), импорт файла (FR-101…FR-103, 13a), удаление по реестру (FR-46) и смена версии (FR-49), двойная
/// установка (FR-47). Последняя папка цели запоминается (FR-40).
/// </summary>
public sealed class CustomPathService
{
    private readonly CustomPathDeps _d;

    public CustomPathService(CustomPathDeps deps) => _d = deps;

    public IReadOnlyList<CustomRoot> Roots => _d.Settings.CustomRoots ?? [];

    public string? LastRoot(RhinoTarget t) => (_d.Settings.LastCustomRoot ?? []).GetValueOrDefault(StandardInstaller.TargetKey(t));

    private void RememberRoot(RhinoTarget t, string root)
    {
        (_d.Settings.LastCustomRoot ??= [])[StandardInstaller.TargetKey(t)] = root;
        _d.Save();
    }

    public bool InStandardProfile(RhinoTarget t, string name) =>
        _d.Scanner.Scan(t.UserPackagesDirectory).Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public bool InCustomPath(RhinoTarget t, string name) =>
        _d.Registry.Custom().Any(r => r.Mode == InstallMode.Custom && r.TargetKey == StandardInstaller.TargetKey(t) && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Окно установки; null — отмена.</summary>
    public Task<InstallChoice?> ChooseAsync(RhinoTarget target, string package, string version, DistributionTag? tag, bool standardAllowed,
        IReadOnlyList<string>? runtimeFolders = null, DependencyReport? dependencies = null) =>
        _d.ShowOptions(new InstallOptionsViewModel(new InstallOptionsContext(target, package, version, tag, standardAllowed,
            InStandardProfile(target, package), InCustomPath(target, package), _d.Targets(), Roots, LastRoot(target))
        {
            RuntimeFolders = runtimeFolders ?? [],
            Dependencies = dependencies ?? DependencyReport.None,
        }, _d.PickFolder));

    /// <summary>Пакет из каталога — в свою папку или «только распаковать».</summary>
    public async Task<UninstallOutcome> InstallFromCatalogAsync(RhinoTarget target, CardInstallRequest req, InstallChoice choice)
    {
        RememberRoot(target, choice.Root!);
        var d = req.Distribution;
        CustomInstallResult? r = null;
        await _d.RunInBackground(async ct =>
        {
            var file = await _d.Files.GetFileAsync(new DownloadRequest(d.Url!, d.Filename ?? Path.GetFileName(new Uri(d.Url!).LocalPath),
                req.Package, req.Version, d.Tag.Original, d.Size, req.PublishedAt), ct).ConfigureAwait(false);
            r = _d.Installer.Install(new CustomInstallRequest(target, choice.Root!, req.Package, req.Version, choice.Mode, YakFile: file.Path, Sha256: file.Sha256,
                Runtime: choice.Runtime));
        }, string.Format(CultureInfo.CurrentCulture, Strings.Card_Installing, req.Package, req.Version)).ConfigureAwait(false);
        _d.Rescan();
        await ShowRhpInstructionAsync(r!).ConfigureAwait(false);
        return new UninstallOutcome(true, Format(r!));
    }

    /// <summary>Импорт файла (FR-101): цель — выбранная установленная версия Rhino.</summary>
    public async Task<UninstallOutcome?> ImportAsync(string file, RhinoTarget? target)
    {
        var plan = FileImport.Plan(file, _d.FileSystem, _d.Motw);
        if (plan.Kind == ImportKind.RhinoInstaller)
        {
            if (_d.RhinoInstaller.Find() is not { } rhiexec)
                return new UninstallOutcome(false, Strings.Imp_NoRhiexec);
            if (!await _d.Dialogs.ConfirmAsync(Strings.Imp_Title, string.Format(CultureInfo.CurrentCulture, Strings.Imp_RhiConfirm, Path.GetFileName(file), rhiexec), Strings.Imp_RhiButton).ConfigureAwait(false))
                return null;
            return _d.RhinoInstaller.Launch(file)
                ? new UninstallOutcome(true, string.Format(CultureInfo.CurrentCulture, Strings.Imp_RhiLaunched, Path.GetFileName(file)))
                : new UninstallOutcome(false, Strings.Imp_NoRhiexec);
        }

        if (plan.Problem is { } problem)
            return new UninstallOutcome(false, string.Format(CultureInfo.CurrentCulture, Strings.Imp_Failed, Path.GetFileName(file), problem));
        if (target is null)
            return new UninstallOutcome(false, Strings.Imp_NeedTarget);

        var tag = plan.Kind == ImportKind.Yak ? TagOfYak(file) : null;
        var choice = await ChooseAsync(target, plan.Name, plan.Version, tag, plan.StandardAllowed, plan.RuntimeFolders, plan.Dependencies).ConfigureAwait(false);
        if (choice is null)
            return null;

        try
        {
            if (choice.Mode == InstallMode.Standard)
            {
                InstallResult? sr = null;
                await _d.RunInBackground(async ct => sr = await _d.Standard.InstallAsync(
                        new InstallRequest(target, plan.Name, plan.Version, file, tag ?? DistributionTag.Parse(null)), ct).ConfigureAwait(false),
                    string.Format(CultureInfo.CurrentCulture, Strings.Card_Installing, plan.Name, plan.Version)).ConfigureAwait(false);
                _d.Rescan();
                return new UninstallOutcome(true, PackageCardViewModel.FormatInstall(sr!));
            }

            RememberRoot(target, choice.Root!);
            CustomInstallResult? r = null;
            await _d.RunInBackground(ct =>
            {
                var prepared = plan.Kind == ImportKind.Yak ? null : FileImport.Prepare(plan, choice.Root!, includeDependencies: choice.IncludeDependencies);
                r = _d.Installer.Install(new CustomInstallRequest(target, choice.Root!, plan.Name, plan.Version, choice.Mode,
                    YakFile: plan.Kind == ImportKind.Yak ? file : null, PreparedDirectory: prepared, Source: plan.Source, SourceFile: file,
                    Runtime: choice.Runtime));
                return Task.CompletedTask;
            }, string.Format(CultureInfo.CurrentCulture, Strings.Imp_Running, Path.GetFileName(file))).ConfigureAwait(false);
            _d.Rescan();
            await ShowRhpInstructionAsync(r!).ConfigureAwait(false);
            return new UninstallOutcome(true, Format(r!));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new UninstallOutcome(false, string.Format(CultureInfo.CurrentCulture, Strings.Imp_Failed, Path.GetFileName(file), e.Message));
        }
    }

    /// <summary>Удаление установки по пользовательскому пути (FR-46, FR-85).</summary>
    public async Task<UninstallOutcome?> UninstallAsync(RhinoTarget target, InstallRecord record)
    {
        var text = string.Format(CultureInfo.CurrentCulture, Strings.Cp_ConfirmUninstall, record.Name, record.Version, record.Path)
                   + (record.Links.Count > 0 ? Environment.NewLine + string.Format(CultureInfo.CurrentCulture, Strings.Cp_ConfirmLinks, string.Join(Environment.NewLine, record.Links)) : "");
        if (!await _d.Dialogs.ConfirmAsync(Strings.Un_Title, text, Strings.Un_Button).ConfigureAwait(false))
            return null;
        try
        {
            CustomUninstallResult? r = null;
            await _d.RunInBackground(ct =>
            {
                r = _d.Installer.Uninstall(target, record.Name, record.Root);
                return Task.CompletedTask;
            }, string.Format(CultureInfo.CurrentCulture, Strings.Un_Removing, record.Name, target.DisplayName)).ConfigureAwait(false);
            _d.Rescan();
            return new UninstallOutcome(true, string.Format(CultureInfo.CurrentCulture, Strings.Cp_Uninstalled, r!.Name, string.Join(", ", r.Versions), r.RemovedLinks.Count));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new UninstallOutcome(false, string.Format(CultureInfo.CurrentCulture, Strings.Un_Failed, e.Message));
        }
    }

    /// <summary>Обновление или откат по пользовательскому пути (FR-49): только пакеты из каталога.</summary>
    public async Task<UninstallOutcome?> ChangeVersionAsync(RhinoTarget target, InstallRecord record, string? version)
    {
        if (record.Source != InstallSource.Catalog)
            return new UninstallOutcome(false, Strings.Cp_ImportNoUpdate);
        try
        {
            var versions = await _d.Files.GetVersionsAsync(record.Name, CancellationToken.None).ConfigureAwait(false);
            var pick = version is null
                ? PackageLifecycle.BestFor(versions, target.Release)
                : versions.FirstOrDefault(v => BuiltInInstaller.SameVersion(v.Version, version)) is { } vi && PackageLifecycle.Pick(vi, target.Release) is { } pd ? (vi, pd) : null;
            if (pick is not { } p)
                return new UninstallOutcome(false, string.Format(CultureInfo.CurrentCulture, Strings.Cp_NoVersion, record.Name, target.DisplayName));
            if (BuiltInInstaller.SameVersion(p.Version.Version, record.Version))
                return new UninstallOutcome(true, Strings.Lc_NothingToUpdate);

            var text = string.Format(CultureInfo.CurrentCulture, Strings.Lc_ConfirmChange, record.Name, record.Version, p.Version.Version, target.DisplayName)
                       + Environment.NewLine + string.Format(CultureInfo.CurrentCulture, Strings.Lc_OldRemoved, record.Version);
            if (!await _d.Dialogs.ConfirmAsync(Strings.Lc_ChangeTitle, text, Strings.Lc_ChangeButton).ConfigureAwait(false))
                return null;

            CustomInstallResult? r = null;
            await _d.RunInBackground(async ct =>
            {
                var d = p.Distribution;
                var file = await _d.Files.GetFileAsync(new DownloadRequest(d.Url!, d.Filename ?? Path.GetFileName(new Uri(d.Url!).LocalPath),
                    record.Name, p.Version.Version, d.Tag.Original, d.Size, p.Version.CreatedAt), ct).ConfigureAwait(false);
                r = _d.Installer.ChangeVersion(record, new CustomInstallRequest(target, record.Root!, record.Name, p.Version.Version, record.Mode,
                    YakFile: file.Path, Sha256: file.Sha256));
            }, string.Format(CultureInfo.CurrentCulture, Strings.Lc_Changing, record.Name, p.Version.Version)).ConfigureAwait(false);
            _d.Rescan();
            var lines = new List<string> { string.Format(CultureInfo.CurrentCulture, Strings.Lc_Changed, record.Name, record.Version, r!.Version, Strings.Cp_ModeCustom) };
            lines.AddRange(r.Warnings);
            return new UninstallOutcome(true, string.Join(Environment.NewLine, lines));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new UninstallOutcome(false, string.Format(CultureInfo.CurrentCulture, Strings.Lc_ChangeFailed, e.Message));
        }
    }

    internal static string Format(CustomInstallResult r)
    {
        var lines = new List<string>
        {
            string.Format(CultureInfo.CurrentCulture, r.Mode == InstallMode.Unpack ? Strings.Cp_Unpacked : Strings.Cp_Installed, r.Name, r.Version, r.VersionDirectory),
        };
        if (r.GhLink is not null)
            lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.Cp_GhLink, r.GhLink));
        if (r.UserObjects.Count > 0)
            lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.Cp_UserObjects, r.UserObjects.Count));
        lines.AddRange(r.Warnings);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>FR-44: .rhp распакованы — инструкция и «Открыть папку».</summary>
    private async Task ShowRhpInstructionAsync(CustomInstallResult r)
    {
        if (r.Mode != InstallMode.Custom || r.RhinoPlugins.Count == 0)
            return;
        var text = string.Format(CultureInfo.CurrentCulture, Strings.Cp_RhpInstruction, string.Join(Environment.NewLine, r.RhinoPlugins));
        if (await _d.Dialogs.ConfirmAsync(Strings.Cp_RhpTitle, text, Strings.Cp_OpenFolder).ConfigureAwait(false))
            _d.OpenFolder(Path.GetDirectoryName(r.RhinoPlugins[0])!);
    }

    private static DistributionTag? TagOfYak(string file)
    {
        // Метка — из имени файла <name>-<ver>-<tag>.yak (FR-61); нет — «любая» (17).
        var stem = Path.GetFileNameWithoutExtension(file);
        var parts = stem.Split('-');
        return parts.Length >= 3 ? DistributionTag.Parse(string.Join('-', parts[^2..])) : null;
    }
}
