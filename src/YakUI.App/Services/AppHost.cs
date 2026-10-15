using Microsoft.Extensions.DependencyInjection;
using YakUI.App.Resources;
using YakUI.App.ViewModels;
using YakUI.Core.Hosts;
using YakUI.Core.Api;
using YakUI.Core.Archive;
using YakUI.Core.Catalog;
using YakUI.Core.Cli;
using YakUI.Core.Compat;
using YakUI.Core.Downloads;
using YakUI.Core.Icons;
using YakUI.Core.Install;
using YakUI.Core.Logging;
using YakUI.Core.Platform;
using YakUI.Core.Settings;
using YakUI.Core.Storage;
using YakUI.Core.Targets;
using YakUI.Core.Tools;
using YakUI.Platform.Windows;

namespace YakUI.App.Services;

/// <summary>Сохранение выбранного пункта списка Rhino в <c>settings.json</c> (FR-81f).</summary>
public sealed class SettingsTargetSelectionStore : ITargetSelectionStore
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;

    public SettingsTargetSelectionStore(AppSettings settings, SettingsStore store)
    {
        _settings = settings;
        _store = store;
    }

    public (string? Key, int? ServiceRelease) Load() => (_settings.LastTargetKey, _settings.LastServiceRelease);

    public void Save(string key, int? serviceRelease)
    {
        if (_settings.LastTargetKey == key && _settings.LastServiceRelease == serviceRelease)
            return;
        _settings.LastTargetKey = key;
        _settings.LastServiceRelease = serviceRelease;
        _store.Save(_settings);
    }
}

/// <summary>
/// Корень композиции GUI: платформа, настройки, база, журнал, лог, ядро и ViewModel.
/// Быстрая часть запуска (поиск Rhino, состав списка) — синхронно; проверка yak — фоновой операцией.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly ServiceProvider _services;

    private AppHost(ServiceProvider services) => _services = services;

    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    public static AppHost Create(IUiDispatcher ui)
    {
        var platform = new WindowsPlatform();
        var settingsStore = new SettingsStore(platform.Paths.ConfigDirectory);
        var settings = settingsStore.Load();
        var log = new FileLog(Path.Combine(platform.Paths.DataDirectory, "logs"), FileLog.ParseLevel(settings.LogLevel));
        log.Info($"YakUI {Core.AppInfo.Version} запущен; данные: {platform.Paths.DataDirectory}");

        var s = new ServiceCollection();
        s.AddSingleton(platform);
        s.AddSingleton<IPlatformPaths>(platform.Paths);
        s.AddSingleton(settingsStore);
        s.AddSingleton(settings);
        s.AddSingleton<IAppLog>(log);
        s.AddSingleton(ui);
        s.AddSingleton(_ =>
        {
            var db = YakUiDatabase.Open(platform.Paths.DatabasePath(), new DatabaseOpenOptions
            {
                OpenLogPath = Path.Combine(platform.Paths.DataDirectory, "logs", "db-open.log"), // 21.7
                Caller = "App",
            });
            if (db.QuarantinedPath is not null)
                log.Warning($"База повреждена ({db.QuarantineReason}); отложена в {db.QuarantinedPath}, создана новая. Кэш каталога будет построен заново.");
            return db;
        });
        s.AddSingleton(sp => new OperationJournal(sp.GetRequiredService<YakUiDatabase>()));
        s.AddSingleton(_ => new DotNetRuntimeTable(settings.DotNetRuntimes!));
        s.AddSingleton(sp => new RhinoDiscovery(platform.Registry, platform.FileSystem, platform.Paths, sp.GetRequiredService<DotNetRuntimeTable>(), settings.ReleaseTypeRules));
        s.AddSingleton(sp => new YakCli(platform.Processes, sp.GetRequiredService<OperationJournal>()));
        s.AddSingleton(sp => new YakDownloadRegistry(sp.GetRequiredService<YakUiDatabase>()));
        s.AddSingleton(sp => new YakToolLibrary(platform.FileSystem, platform.Paths, platform.Signatures, platform.DotNet, sp.GetRequiredService<YakCli>(), settings)
        {
            TrustedDownload = sp.GetRequiredService<YakDownloadRegistry>().IsTrusted, // FR-17: неподписанный, скачанный YakUI
        });
        s.AddSingleton(_ => new YakApiClient(new YakApiOptions { BaseUri = new Uri(settings.ApiBaseUrl!), MaxParallelism = settings.ApiParallelism }));
        s.AddSingleton(sp => new CatalogService(sp.GetRequiredService<YakApiClient>(), sp.GetRequiredService<YakUiDatabase>(), TimeSpan.FromMinutes(settings.CatalogIndexTtlMinutes)));
        s.AddSingleton(sp => new CatalogCrawler(sp.GetRequiredService<CatalogService>(), new CrawlerOptions { MaxConcurrency = settings.ApiParallelism }, log));
        s.AddSingleton<CatalogStatusViewModel>();
        s.AddSingleton(_ => new InstalledVersions(new ProfileScanner(platform.FileSystem)));
        s.AddSingleton<TargetsHolder>();
        s.AddSingleton<ICatalogSource>(sp => new CatalogSource(sp));
        s.AddSingleton(sp => new YakInspectionService(sp.GetRequiredService<YakUiDatabase>(), log: log));
        s.AddSingleton<IPackageCardSource>(sp => new PackageCardSource(sp));
        s.AddSingleton(sp => new IconService(
            Path.Combine(platform.Paths.DataDirectory, "icons"),
            settings.ResolveDownloadsDirectory(platform.Paths.DataDirectory),
            log: log,
            journal: sp.GetRequiredService<OperationJournal>(),
            scaler: platform.Images));
        s.AddSingleton(sp => new IconLoader(sp.GetRequiredService<IconService>(), maxParallel: 4, log));
        s.AddSingleton<IIconProvider>(sp => new IconProvider(sp.GetRequiredService<IconLoader>(), sp.GetRequiredService<IUiDispatcher>(), sp.GetRequiredService<IconService>()));
        s.AddSingleton<CatalogViewModel>();
        s.AddSingleton(sp => new InstalledPackagesService(new ProfileScanner(platform.FileSystem), sp.GetRequiredService<YakToolLibrary>()));
        s.AddSingleton(sp => new InstalledOverviewService(platform.FileSystem, sp.GetRequiredService<InstalledPackagesService>(), platform.RhinoProcesses));
        s.AddSingleton<IInstalledSource>(sp => new InstalledSource(sp));
        s.AddSingleton(sp => new InstalledViewModel(sp.GetRequiredService<IInstalledSource>(), sp.GetRequiredService<TargetSelectorViewModel>(),
            sp.GetRequiredService<IIconProvider>(), sp.GetRequiredService<IUiDispatcher>(), sp.GetRequiredService<UninstallCoordinator>(),
            sp.GetRequiredService<LifecycleCoordinator>(), sp.GetRequiredService<CustomPathService>(),
            () => AppShell.PickFileAsync(Strings.Imp_Title, "Rhino / Grasshopper", "*.yak;*.zip;*.gha;*.ghpy;*.ghuser;*.rhp;*.rhi"),
            () => AppShell.PickFolderAsync(Strings.Imp_AddFolder)));
        s.AddSingleton(sp => new Downloader(settings.ResolveDownloadsDirectory(platform.Paths.DataDirectory),
            new DownloadRegistry(sp.GetRequiredService<YakUiDatabase>()), log: log, journal: sp.GetRequiredService<OperationJournal>()));
        s.AddSingleton(sp => new DownloadQueue(sp.GetRequiredService<Downloader>()));
        s.AddSingleton(sp => new RemoteFileChecker(journal: sp.GetRequiredService<OperationJournal>(), log: log));
        s.AddSingleton(sp => new InstallRegistry(sp.GetRequiredService<YakUiDatabase>()));
        s.AddSingleton(sp => new StandardInstaller(sp.GetRequiredService<YakToolLibrary>(), sp.GetRequiredService<YakCli>(),
            new ProfileScanner(platform.FileSystem), sp.GetRequiredService<InstallRegistry>(), platform.RhinoProcesses,
            sp.GetRequiredService<OperationJournal>(), log));
        s.AddSingleton(sp => new StandardUninstaller(sp.GetRequiredService<YakToolLibrary>(), sp.GetRequiredService<YakCli>(),
            new ProfileScanner(platform.FileSystem), sp.GetRequiredService<InstallRegistry>(), platform.RhinoProcesses,
            sp.GetRequiredService<OperationJournal>(), log));
        s.AddSingleton(sp => new ProfileWatchService(platform.Watcher, () => sp.GetRequiredService<AppHost.Rescanner>().Rescan(), log));
        s.AddSingleton(sp => new Rescanner(sp));
        s.AddSingleton<IUninstallBackend>(sp => new UninstallBackend(sp));
        s.AddSingleton(sp => new CustomInstaller(sp.GetRequiredService<InstallRegistry>(), platform.RhinoProcesses, platform.MarkOfTheWeb,
            sp.GetRequiredService<PendingRemovals>(), sp.GetRequiredService<OperationJournal>(),
            rhinoInstallPaths: () => sp.GetRequiredService<TargetsHolder>().Targets.Select(t => t.InstallPath)));
        s.AddSingleton(sp => new CustomPathService(new CustomPathDeps(
            sp.GetRequiredService<CustomInstaller>(), sp.GetRequiredService<StandardInstaller>(), sp.GetRequiredService<InstallRegistry>(),
            new ProfileScanner(platform.FileSystem), sp.GetRequiredService<IPackageFiles>(), platform.FileSystem, platform.MarkOfTheWeb,
            platform.RhinoInstaller, sp.GetRequiredService<IDialogService>(), settings, () => sp.GetRequiredService<SettingsStore>().Save(settings),
            () => sp.GetRequiredService<TargetsHolder>().Targets, AppShell.ShowInstallOptionsAsync,
            async (work, title) =>
            {
                Exception? error = null;
                using (sp.GetRequiredService<ProfileWatchService>().Suppress())
                {
                    await sp.GetRequiredService<BackgroundOperations>().RunAsync(title, async ctx =>
                    {
                        try
                        {
                            await work(ctx.Token).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            error = e;
                            throw;
                        }
                    }, cancellable: false).ConfigureAwait(false);
                }

                if (error is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
            },
            () => sp.GetRequiredService<Rescanner>().Rescan(),
            path => platform.Folders.Open(path),
            () => AppShell.PickFolderAsync(Strings.Set_PickRoot))));
        s.AddSingleton(sp => new PinRegistry(sp.GetRequiredService<YakUiDatabase>()));
        s.AddSingleton(sp => new PendingRemovals(sp.GetRequiredService<YakUiDatabase>(), platform.RhinoProcesses, sp.GetRequiredService<OperationJournal>()));
        s.AddSingleton(sp => new VersionChanger(sp.GetRequiredService<StandardInstaller>(), new ProfileScanner(platform.FileSystem),
            sp.GetRequiredService<InstallRegistry>(), platform.RhinoProcesses, sp.GetRequiredService<OperationJournal>(), log,
            sp.GetRequiredService<PendingRemovals>()));
        s.AddSingleton<IPackageFiles>(sp => new PackageFiles(sp));
        s.AddSingleton(sp => new PackageLifecycle(sp.GetRequiredService<IPackageFiles>(), sp.GetRequiredService<VersionChanger>(),
            new ProfileScanner(platform.FileSystem), sp.GetRequiredService<PinRegistry>(), sp.GetRequiredService<OperationJournal>()));
        s.AddSingleton(_ => new RhinoAutoUpdateReader(platform.FileSystem, platform.Paths));
        s.AddSingleton<ILifecycleBackend>(sp => new LifecycleBackend(sp));
        s.AddSingleton<LifecycleCoordinator>();
        s.AddSingleton<UninstallCoordinator>();
        s.AddSingleton<IDialogService, AvaloniaDialogService>();
        s.AddSingleton<IDownloadsSource>(sp => new DownloadsSource(sp));
        s.AddSingleton<DownloadsViewModel>();
        s.AddSingleton<ITargetSelectionStore, SettingsTargetSelectionStore>();
        s.AddSingleton<BackgroundOperations>();
        s.AddSingleton<TargetSelectorViewModel>();
        s.AddSingleton<IJournalSource>(sp => new JournalSource(sp));
        s.AddSingleton<JournalViewModel>();
        s.AddSingleton(sp => new YakService(sp.GetRequiredService<YakToolLibrary>(),
            () => new YakDownloader(platform.Paths, platform.Signatures, platform.MarkOfTheWeb, sp.GetRequiredService<YakCli>(),
                sp.GetRequiredService<YakDownloadRegistry>(), journal: sp.GetRequiredService<OperationJournal>(), log: log),
            () => new YakUpdateChecker(journal: sp.GetRequiredService<OperationJournal>()),
            sp.GetRequiredService<BackgroundOperations>(), settings, () => sp.GetRequiredService<SettingsStore>().Save(settings),
            () => sp.GetRequiredService<TargetsHolder>().Targets, log));
        s.AddSingleton<IFirstRunBackend>(sp => new FirstRunBackend(sp));
        s.AddSingleton<ISettingsBackend>(sp => new SettingsBackend(sp));
        s.AddSingleton(sp => new HostsSession(platform.Hosts.HostsPath, platform.Paths.DataDirectory, sp.GetRequiredService<OperationJournal>()));
        s.AddSingleton<IHostsDialogs, AvaloniaHostsDialogs>();
        s.AddSingleton(sp => new HostsViewModel(platform.Hosts, sp.GetRequiredService<HostsSession>(), settings,
            () => sp.GetRequiredService<SettingsStore>().Save(settings), sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IHostsDialogs>(), sp.GetRequiredService<IUiDispatcher>(), sp.GetRequiredService<OperationJournal>()));
        s.AddSingleton<SettingsViewModel>();
        s.AddSingleton<MainWindowViewModel>();

        return new AppHost(s.BuildServiceProvider());
    }

    /// <summary>Поиск Rhino, состав списка Rhino, затем фоновые проверка yak-инструментов и построение кэша каталога.</summary>
    public void Start()
    {
        var log = Get<IAppLog>();
        var settings = Get<AppSettings>();
        var ui = Get<IUiDispatcher>();
        var targets = Get<RhinoDiscovery>().Discover();
        foreach (var t in targets)
            log.Info($"Найден {t.DisplayName}: {t.InstallPath}");

        Get<TargetsHolder>().Targets = targets;
        AppShell.ApplyTheme(settings.Theme); // FR-83
        YakUI.Core.Net.SwitchableProxy.Shared.Settings = settings.Proxy ?? new(); // FR-95
        RebuildTargetList(targets);
        ShowCatalogState(offline: false);

        // 14.2: наблюдение за hosts (FR-89g) и восстановление после сбоя или отказа от UAC (FR-89f).
        var hosts = Get<HostsViewModel>();
        _hostsWatch = Get<WindowsPlatform>().Watcher.Watch(Path.GetDirectoryName(Get<WindowsPlatform>().Hosts.HostsPath)!, _ => hosts.OnHostsChanged());
        ui.Post(() => _ = hosts.CheckPendingRestoreAsync());
        _ = Get<CatalogViewModel>().ReloadAsync();

        // FR-55a: наблюдение за профилями выбранной цели (при «Любой версии» — всех установленных).
        var selector = Get<TargetSelectorViewModel>();
        void Watch() => Get<ProfileWatchService>().WatchTargets(
            selector.Current is { Kind: TargetChoiceKind.Installed, Target: { } t } ? [t]
            : selector.Current is { Kind: TargetChoiceKind.AnyVersion } ? targets : []);
        selector.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TargetSelectorViewModel.SelectedItem))
                Watch();
        };
        Watch();

        // FR-20, FR-21: фоновое построение кэша; каталог работает и до его окончания.
        var status = Get<CatalogStatusViewModel>();
        _ = Get<BackgroundOperations>().RunAsync(Strings.Op_Crawl, async ctx =>
        {
            var lastReload = 0;
            var progress = new ImmediateProgress<CrawlProgress>(p =>
            {
                // Каталог доступен во время построения кэша (FR-21): список перечитывается каждые 300 пакетов.
                if (p.Done - Volatile.Read(ref lastReload) >= 300)
                {
                    Volatile.Write(ref lastReload, p.Done);
                    _ = Get<CatalogViewModel>().ReloadAsync();
                }

                ctx.Report(p.Total == 0 ? 1 : (double)p.Done / p.Total,
                    p.Throttled ? Strings.Op_CrawlThrottled : string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Op_CrawlDetail, p.Done, p.Total, p.Concurrency));
                ui.Post(() => status.ShowProgress(p));
            });
            var result = await Get<CatalogCrawler>().RunAsync(progress, ctx.Token);
            ui.Post(() =>
            {
                RebuildTargetList(targets);
                ShowCatalogState(result.IndexOffline);
                _ = Get<CatalogViewModel>().ReloadAsync();
            });
        });

        _ = Get<BackgroundOperations>().RunAsync(Strings.Op_CheckYak, async ctx =>
        {
            var tools = await Get<YakToolLibrary>().RefreshAsync(targets, ctx.Token);
            ui.Post(() => Get<SettingsViewModel>().RebuildYak());

            // FR-18: мастер первого запуска; FR-12: проверка новых сборок yak — не чаще раза в сутки.
            if (!settings.FirstRunDone)
                ui.Post(() => _ = AppShell.ShowWizardAsync(new FirstRunViewModel(Get<IFirstRunBackend>())));
            else if (Get<YakService>().IsCheckDue)
                _ = Get<BackgroundOperations>().RunAsync(Strings.Op_YakCheck, async c2 =>
                {
                    await Get<YakService>().CheckAsync(c2.Token);
                    ui.Post(() => Get<SettingsViewModel>().RebuildYak());
                });
            foreach (var tool in tools)
                log.Info($"yak {tool.Version?.ToString() ?? "?"} ({tool.Path}): привязка {tool.BoundRhinoMajor?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}{(tool.Problem is null ? "" : "; не используется: " + tool.Problem)}");
        });

        void RebuildTargetList(IReadOnlyList<RhinoTarget> installed)
        {
            var releases = Get<CatalogService>().GetKnownRhinoReleases();

            // FR-81b: major-версии из тегов каталога пополняют список в настройках.
            var newMajors = TargetChoiceBuilder.NewMajors(settings.RhinoMajors!, releases, installed.Select(t => t.Major));
            if (newMajors.Count > 0)
            {
                settings.RhinoMajors!.AddRange(newMajors);
                Get<SettingsStore>().Save(settings);
                log.Info($"Новые версии Rhino из каталога: {string.Join(", ", newMajors)}");
            }

            Get<TargetSelectorViewModel>().Rebuild(TargetChoiceBuilder.Build(installed, settings.RhinoMajors!, releases));
        }

        void ShowCatalogState(bool offline)
        {
            var catalog = Get<CatalogService>();
            var (packages, _, _) = catalog.GetCacheStats();
            if (catalog.IndexFetchedAt is { } fetched && packages > 0)
                Get<CatalogStatusViewModel>().ShowReady(packages, catalog.GetPendingPackages().Count, fetched, offline, DateTimeOffset.Now);
        }
    }

    /// <summary>Источник данных экрана «Каталог»: кэш, установленные версии по цели, приоритет краулера.</summary>
    private sealed class CatalogSource(IServiceProvider sp) : ICatalogSource
    {
        public CatalogSnapshot LoadSnapshot() => sp.GetRequiredService<CatalogService>().LoadSnapshot();

        public IReadOnlyDictionary<string, string> InstalledFor(TargetChoice? choice) =>
            sp.GetRequiredService<InstalledVersions>().For(choice, sp.GetRequiredService<AppHost.TargetsHolder>().Targets);

        public void Prioritize(IEnumerable<string> names) => sp.GetRequiredService<CatalogCrawler>().Prioritize(names);
    }

    /// <summary>Источник карточки пакета: версии из кэша, владельцы с сервера, инспекция .yak, ссылки.</summary>
    private sealed class PackageCardSource(IServiceProvider sp) : IPackageCardSource
    {
        public Task<CatalogResult<IReadOnlyList<PackageVersionInfo>>> GetVersionsAsync(string name, CancellationToken ct) =>
            sp.GetRequiredService<CatalogService>().GetVersionsAsync(name, ct);

        public Task<IReadOnlyList<PackageOwner>> GetOwnersAsync(string name, CancellationToken ct) =>
            sp.GetRequiredService<YakApiClient>().GetOwnersAsync(name, ct);

        public Task<YakInspection> InspectAsync(string url, string? filename, CancellationToken ct) =>
            sp.GetRequiredService<YakInspectionService>().InspectAsync(url, filename, ct);

        public bool OpenUrl(string? url) => sp.GetRequiredService<WindowsPlatform>().Urls.TryOpen(url);

        public void Download(DownloadRequest request) => sp.GetRequiredService<DownloadQueue>().Enqueue(request);

        public bool IsInspectionCached(string url) => sp.GetRequiredService<YakInspectionService>().GetCached(url) is not null;

        public Task<RemoteFileState> CheckRemoteAsync(string url, string? filename, CancellationToken ct) =>
            sp.GetRequiredService<RemoteFileChecker>().CheckAsync(url, filename, ct);

        public void RecordRemote(string url, RemoteFileState state, string? filename) =>
            sp.GetRequiredService<RemoteFileChecker>().Record(url, state, filename);

        public bool IsDownloaded(string url) => sp.GetRequiredService<Downloader>().FindExisting(url) is not null;

        public bool IsInstallableTarget(RhinoRelease? target) => FindTarget(target) is not null;

        private RhinoTarget? FindTarget(RhinoRelease? release) => release is { } r
            ? sp.GetRequiredService<TargetsHolder>().Targets.Where(t => t.Release == r).MaxBy(t => t.Version)
            : null;

        public bool IsPinned(string package, RhinoRelease? release) =>
            FindTarget(release) is { } t && sp.GetRequiredService<PinRegistry>().Find(StandardInstaller.TargetKey(t), package) is not null;

        public async Task<UninstallOutcome?> ChangeVersionAsync(string package, string? from, string version, RhinoRelease release) =>
            FindTarget(release) is { } t
                ? await sp.GetRequiredService<LifecycleCoordinator>().ChangeVersionAsync(t, package, from, version)
                : new UninstallOutcome(false, Strings.Card_InstallTargetHint);

        public async Task<UninstallOutcome?> TogglePinAsync(string package, string? version, RhinoRelease release)
        {
            if (FindTarget(release) is not { } t)
                return new UninstallOutcome(false, Strings.Card_InstallTargetHint);
            var lc = sp.GetRequiredService<LifecycleCoordinator>();
            return IsPinned(package, release) ? lc.Unpin(t, package) : await lc.PinAsync(t, package, version);
        }

        public async Task<UninstallOutcome?> UninstallAsync(string package, RhinoRelease release)
        {
            var target = FindTarget(release);
            return target is null
                ? new UninstallOutcome(false, Strings.Card_InstallTargetHint)
                : await sp.GetRequiredService<UninstallCoordinator>().UninstallAsync(target, package);
        }

        public async Task<UninstallOutcome?> InstallWithOptionsAsync(CardInstallRequest request)
        {
            if (FindTarget(request.Target) is not { } target)
                return new UninstallOutcome(false, Strings.Card_InstallTargetHint);
            var paths = sp.GetRequiredService<CustomPathService>();
            var choice = await paths.ChooseAsync(target, request.Package, request.Version, request.Distribution.Tag, standardAllowed: true,
                runtimeFolders: request.Frameworks);
            if (choice is null)
                return null;
            try
            {
                return choice.Mode == InstallMode.Standard
                    ? new UninstallOutcome(true, PackageCardViewModel.FormatInstall(await InstallAsync(request, CancellationToken.None)))
                    : await paths.InstallFromCatalogAsync(target, request, choice);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return new UninstallOutcome(false, string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Card_InstallFailed, e.Message));
            }
        }

        public async Task<InstallResult> InstallAsync(CardInstallRequest request, CancellationToken ct)
        {
            using var quiet = sp.GetRequiredService<ProfileWatchService>().Suppress();
            var target = FindTarget(request.Target) ?? throw new InstallFailedException(Strings.Card_InstallTargetHint);
            var d = request.Distribution;
            InstallResult? result = null;
            Exception? error = null;
            await sp.GetRequiredService<BackgroundOperations>().RunAsync(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Card_Installing, request.Package, request.Version),
                async ctx =>
                {
                    try
                    {
                        // Файл — из папки загрузок или скачивается (FR-36, FR-37a: удалённый с сервера — только из загрузок).
                        var downloader = sp.GetRequiredService<Downloader>();
                        var record = (d.Url is { } url ? downloader.FindExisting(url) : null)
                            ?? await downloader.DownloadAsync(
                                new DownloadRequest(d.Url ?? throw new InstallFailedException(Strings.Err_NoDistributionUrl),
                                    d.Filename ?? Path.GetFileName(new Uri(d.Url).LocalPath), request.Package, request.Version, d.Tag.Original, d.Size, request.PublishedAt),
                                new ImmediateProgress<DownloadProgress>(p => ctx.Report(p.Total is > 0 ? (double)p.Done / p.Total.Value : null)),
                                ctx.Token).ConfigureAwait(false);
                        ctx.Report(null, record.Filename);
                        result = await sp.GetRequiredService<StandardInstaller>().InstallAsync(
                            new InstallRequest(target, request.Package, request.Version, record.Path, d.Tag, record.Sha256), ctx.Token).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        error = e;
                        throw;
                    }
                }).ConfigureAwait(false);

            if (error is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
            if (result is null)
                throw new OperationCanceledException();

            sp.GetRequiredService<Rescanner>().Rescan();
            return result;
        }
    }

    /// <summary>Пересканирование по FR-55a: «Установленные» (вместе со сверкой реестра) и метки каталога.</summary>
    internal sealed class Rescanner(IServiceProvider sp)
    {
        public void Rescan() => sp.GetRequiredService<IUiDispatcher>().Post(() =>
        {
            sp.GetRequiredService<InstalledViewModel>().Refresh();
            _ = sp.GetRequiredService<CatalogViewModel>().ReloadAsync();
        });
    }

    /// <summary>Удаление фоновой операцией; наблюдатель на это время приглушён, затем — пересканирование.</summary>
    private sealed class UninstallBackend(IServiceProvider sp) : IUninstallBackend
    {
        public UninstallPreview Preview(RhinoTarget target, string name) => sp.GetRequiredService<StandardUninstaller>().Preview(target, name);

        public async Task<UninstallResult> UninstallAsync(RhinoTarget target, string name)
        {
            UninstallResult? result = null;
            Exception? error = null;
            using (sp.GetRequiredService<ProfileWatchService>().Suppress())
            {
                await sp.GetRequiredService<BackgroundOperations>().RunAsync(
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Un_Removing, name, target.DisplayName),
                    async ctx =>
                    {
                        try
                        {
                            result = await sp.GetRequiredService<StandardUninstaller>().UninstallAsync(target, name, ctx.Token).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            error = e;
                            throw;
                        }
                    }, cancellable: false).ConfigureAwait(false);
            }

            sp.GetRequiredService<Rescanner>().Rescan();
            if (error is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
            return result ?? throw new OperationCanceledException();
        }
    }

    /// <summary>Версии из кэша каталога, файлы — из папки загрузок или скачанные (этап 4).</summary>
    private sealed class PackageFiles(IServiceProvider sp) : IPackageFiles
    {
        public async Task<IReadOnlyList<PackageVersionInfo>> GetVersionsAsync(string name, CancellationToken ct) =>
            (await sp.GetRequiredService<CatalogService>().GetVersionsAsync(name, ct).ConfigureAwait(false)).Value;

        public async Task<DownloadRecord> GetFileAsync(DownloadRequest request, CancellationToken ct)
        {
            var downloader = sp.GetRequiredService<Downloader>();
            return downloader.FindExisting(request.Url) ?? await downloader.DownloadAsync(request, null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Обновление, откат, «Обновить всё» — фоновыми операциями; наблюдатель приглушён, затем пересканирование.</summary>
    private sealed class LifecycleBackend(IServiceProvider sp) : ILifecycleBackend
    {
        public IReadOnlyDictionary<string, PinRecord> Pins(RhinoTarget target) =>
            sp.GetRequiredService<PinRegistry>().ForTarget(StandardInstaller.TargetKey(target));

        public AutoUpdateState AutoUpdate(RhinoTarget target) => sp.GetRequiredService<RhinoAutoUpdateReader>().Read(target.Major);

        public Task<VersionChangeResult> ChangeVersionAsync(RhinoTarget target, string name, string? version) =>
            Run(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Lc_Changing, name, version ?? "…"),
                ct => sp.GetRequiredService<PackageLifecycle>().ChangeVersionAsync(target, name, version, null, ct), cancellable: false);

        public Task<IReadOnlyList<BulkItemResult>> UpdateAllAsync(RhinoTarget target, IReadOnlyCollection<string>? only) =>
            Run(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Lc_Updating, target.DisplayName),
                ct => sp.GetRequiredService<PackageLifecycle>().UpdateAllAsync(target, only, null, ct), cancellable: true);

        public void Pin(RhinoTarget target, string name)
        {
            sp.GetRequiredService<PackageLifecycle>().Pin(target, name);
            sp.GetRequiredService<Rescanner>().Rescan();
        }

        public void Unpin(RhinoTarget target, string name)
        {
            sp.GetRequiredService<PackageLifecycle>().Unpin(target, name);
            sp.GetRequiredService<Rescanner>().Rescan();
        }

        private async Task<T> Run<T>(string title, Func<CancellationToken, Task<T>> work, bool cancellable)
        {
            T? result = default;
            Exception? error = null;
            using (sp.GetRequiredService<ProfileWatchService>().Suppress())
            {
                await sp.GetRequiredService<BackgroundOperations>().RunAsync(title, async ctx =>
                {
                    try
                    {
                        result = await work(ctx.Token).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        error = e;
                        throw;
                    }
                }, cancellable).ConfigureAwait(false);
            }

            sp.GetRequiredService<Rescanner>().Rescan();
            if (error is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
            return result ?? throw new OperationCanceledException();
        }
    }

    /// <summary>Источник экрана «Журнал» (FR-90).</summary>
    private sealed class JournalSource(IServiceProvider sp) : IJournalSource
    {
        public IReadOnlyList<JournalEntry> ReadLatest(int count) => sp.GetRequiredService<OperationJournal>().ReadLatest(count);

        public string LogsDirectory => Path.Combine(sp.GetRequiredService<WindowsPlatform>().Paths.DataDirectory, "logs");

        public bool OpenFolder(string path) => sp.GetRequiredService<WindowsPlatform>().Folders.Open(path);
    }

    /// <summary>«Настройки» (FR-83, FR-35, FR-13): сохранение в settings.json, применение без перезапуска.</summary>
    private sealed class SettingsBackend(IServiceProvider sp) : ISettingsBackend
    {
        public AppSettings Settings => sp.GetRequiredService<AppSettings>();

        public void Save() => sp.GetRequiredService<SettingsStore>().Save(Settings);

        public IReadOnlyList<RhinoTarget> Targets => sp.GetRequiredService<TargetsHolder>().Targets;

        public IReadOnlyList<YakUI.Core.Tools.YakTool> Tools => sp.GetRequiredService<YakToolLibrary>().Tools;

        public YakUI.Core.Tools.YakTool? SelectFor(int rhinoMajor) => sp.GetRequiredService<YakToolLibrary>().SelectFor(rhinoMajor);

        public Task RefreshYakAsync() => sp.GetRequiredService<BackgroundOperations>().RunAsync(Strings.Op_CheckYak, async ctx =>
        {
            var tools = await sp.GetRequiredService<YakToolLibrary>().RefreshAsync(Targets, ctx.Token).ConfigureAwait(false);
            foreach (var tool in tools)
                sp.GetRequiredService<IAppLog>().Info($"yak {tool.Version?.ToString() ?? "?"} ({tool.Path}): привязка {tool.BoundRhinoMajor?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}{(tool.Problem is null ? "" : "; не используется: " + tool.Problem)}");
        });

        public void ApplyTheme(AppTheme theme) => AppShell.ApplyTheme(theme);

        public string DefaultDownloadsDirectory => Path.Combine(sp.GetRequiredService<WindowsPlatform>().Paths.DataDirectory, "downloads");

        public string? ApplyDownloadsDirectory(string path)
        {
            if (sp.GetRequiredService<DownloadQueue>().Items.Any(i => !i.IsFinished))
                return Strings.Set_DownloadsBusy;
            try
            {
                var full = Path.GetFullPath(path);
                Directory.CreateDirectory(full);
                var probe = Path.Combine(full, ".yakui-write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                sp.GetRequiredService<Downloader>().Directory = full;
                sp.GetRequiredService<IconService>().DownloadsDirectory = full;
                sp.GetRequiredService<OperationJournal>().Write("settings", "downloads-directory", "changed", details: new { path = full });
                sp.GetRequiredService<IUiDispatcher>().Post(() => sp.GetRequiredService<DownloadsViewModel>().Reload());
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return e.Message;
            }
        }

        public Task<string?> PickFolderAsync(string title) => AppShell.PickFolderAsync(title);

        public YakCheckResult? LastYakCheck => sp.GetRequiredService<YakService>().LastCheck;

        public async Task<YakCheckResult> CheckYakUpdatesAsync()
        {
            YakCheckResult? result = null;
            await sp.GetRequiredService<BackgroundOperations>().RunAsync(Strings.Op_YakCheck,
                async ctx => result = await sp.GetRequiredService<YakService>().CheckAsync(ctx.Token).ConfigureAwait(false)).ConfigureAwait(false);
            return result ?? throw new InvalidOperationException(Strings.Set_YakCheck);
        }

        public Task<string> DownloadYakAsync(string version) => DownloadYak(sp, version);

        public string? CheckWritable(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                var probe = Path.Combine(path, ".yakui-write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return e.Message;
            }
        }

        public void OpenFirstRunWizard() => _ = AppShell.ShowWizardAsync(new FirstRunViewModel(sp.GetRequiredService<IFirstRunBackend>()));

        public Task<string?> PickYakAsync(string title) => AppShell.PickFileAsync(title, "yak.exe", "*.exe");

        public bool OpenFolder(string path) => sp.GetRequiredService<WindowsPlatform>().Folders.Open(path);

        public void ApplyProxy(YakUI.Core.Net.ProxySettings proxy)
        {
            YakUI.Core.Net.SwitchableProxy.Shared.Settings = proxy;
            sp.GetRequiredService<OperationJournal>().Write("settings", "proxy", "changed",
                details: new { mode = proxy.Mode.ToString(), proxy = proxy.ManualUri?.ToString() });
        }

        public Task<YakUI.Core.Net.ConnectionReport> CheckConnectionAsync(YakUI.Core.Net.ProxySettings proxy)
        {
            // Первые байты настоящего .yak: дистрибутив самого скачиваемого пакета из кэша каталога.
            var file = sp.GetRequiredService<CatalogService>().LoadSnapshot().Packages
                .OrderByDescending(p => p.DownloadCount)
                .SelectMany(p => p.Versions.SelectMany(v => (v.Files ?? []).Select(f => f.Url ?? "")))
                .FirstOrDefault(u => u.StartsWith("https://files.mcneel.com/", StringComparison.OrdinalIgnoreCase));
            return new YakUI.Core.Net.ConnectionChecker(sp.GetRequiredService<OperationJournal>())
                .CheckAsync(proxy, file is null ? null : new Uri(file));
        }
    }

    /// <summary>Скачивание yak фоновой операцией; переключение — когда других операций нет (FR-19).</summary>
    private static async Task<string> DownloadYak(IServiceProvider sp, string version)
    {
        string? message = null;
        Exception? error = null;
        var ops = sp.GetRequiredService<BackgroundOperations>();
        await ops.RunAsync(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Op_YakDownload, version), async ctx =>
        {
            try
            {
                message = await sp.GetRequiredService<YakService>().DownloadAsync(version, () => ops.RunningCount > 1, ctx.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                error = e;
                throw;
            }
        }).ConfigureAwait(false);
        if (error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        sp.GetRequiredService<IUiDispatcher>().Post(() => sp.GetRequiredService<SettingsViewModel>().RebuildYak());
        return message ?? "";
    }

    /// <summary>Мастер первого запуска (FR-18).</summary>
    private sealed class FirstRunBackend(IServiceProvider sp) : IFirstRunBackend
    {
        public IReadOnlyList<RhinoTarget> Targets => sp.GetRequiredService<TargetsHolder>().Targets;

        public IReadOnlyList<YakTool> Tools => sp.GetRequiredService<YakToolLibrary>().Tools;

        public YakTool? SelectFor(int rhinoMajor) => sp.GetRequiredService<YakToolLibrary>().SelectFor(rhinoMajor);

        public YakCheckResult? LastCheck => sp.GetRequiredService<YakService>().LastCheck;

        public Task<YakCheckResult> CheckAsync() => sp.GetRequiredService<ISettingsBackend>().CheckYakUpdatesAsync();

        public Task<string> DownloadAsync(string version) => DownloadYak(sp, version);

        public void MarkDone()
        {
            var settings = sp.GetRequiredService<AppSettings>();
            settings.FirstRunDone = true;
            sp.GetRequiredService<SettingsStore>().Save(settings);
        }
    }

    /// <summary>Источник экрана «Загрузки». Удаляется только файл в папке загрузок и его запись в реестре.</summary>
    private sealed class DownloadsSource(IServiceProvider sp) : IDownloadsSource
    {
        public DownloadQueue Queue => sp.GetRequiredService<DownloadQueue>();

        public string Directory => sp.GetRequiredService<Downloader>().Directory;

        public IReadOnlyList<DownloadRecord> Library() => new DownloadRegistry(sp.GetRequiredService<YakUiDatabase>()).All();

        public DateTimeOffset? PublishedAt(DownloadRecord record) =>
            record.PublishedAt
            ?? (record.Package is { } name && sp.GetRequiredService<CatalogViewModel>().Snapshot.ByName.TryGetValue(name, out var p)
                ? p.Versions.FirstOrDefault(v => v.Text == record.Version)?.CreatedAt
                : null);

        public void Forget(DownloadRecord record)
        {
            var root = Path.GetFullPath(Directory).TrimEnd('\\') + "\\";
            var file = Path.GetFullPath(record.Path);
            if (file.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(file))
                File.Delete(file);
            new DownloadRegistry(sp.GetRequiredService<YakUiDatabase>()).Remove(record.Id);
            sp.GetRequiredService<OperationJournal>().Write("download", record.Filename, "deleted", details: new { record.Path, record.Sha256 });
        }

        public bool OpenFolder(string path) => sp.GetRequiredService<WindowsPlatform>().Folders.Open(path);

        public bool Reveal(string file) => sp.GetRequiredService<WindowsPlatform>().Folders.Reveal(file);
    }

    /// <summary>Источник экрана «Установленные».</summary>
    private sealed class InstalledSource(IServiceProvider sp) : IInstalledSource
    {
        public IReadOnlyList<RhinoTarget> Targets => sp.GetRequiredService<TargetsHolder>().Targets;

        public async Task<InstalledOverview> GetAsync(RhinoTarget target, CancellationToken ct)
        {
            // FR-49: отложенные удаления прежних версий — когда Rhino закрыт (сканирование идёт и по FR-55a).
            foreach (var removed in sp.GetRequiredService<PendingRemovals>().Process())
                sp.GetRequiredService<IAppLog>().Info($"Отложенное удаление выполнено: {removed}");

            var overview = await sp.GetRequiredService<InstalledOverviewService>().GetAsync(target, ct).ConfigureAwait(false);

            // Этап 3: установки по пользовательскому пути из реестра (FR-46); FR-47 — двойная установка.
            var key = StandardInstaller.TargetKey(target);
            var custom = sp.GetRequiredService<InstallRegistry>().Custom().Where(r => r.TargetKey == key).ToList();
            if (custom.Count > 0)
            {
                var customNames = custom.Where(r => r.Mode == InstallMode.Custom).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var standardNames = overview.Items.Where(i => i.Scope == InstalledScope.User).Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var items = overview.Items.Select(i => i.Scope == InstalledScope.User && customNames.Contains(i.Name) ? i with { IsDuplicate = true } : i)
                    .Concat(custom.Select(r => InstalledItem.FromCustom(target, r) with { IsDuplicate = r.Mode == InstallMode.Custom && standardNames.Contains(r.Name) }))
                    .ToList();
                overview = overview with { Items = items };
            }

            // FR-55a: записи реестра, чьих папок больше нет (кто бы ни удалил пакет), снимаются.
            foreach (var r in sp.GetRequiredService<InstallRegistry>().RemoveStale(Directory.Exists))
            {
                sp.GetRequiredService<OperationJournal>().Write("install-registry", $"{r.Name} {r.Version}", "stale-removed", details: new { r.TargetKey, r.Path, r.Method });
                sp.GetRequiredService<IAppLog>().Info($"Реестр установок: снята устаревшая запись {r.Name} {r.Version} ({r.Path}).");
            }

            return overview;
        }

        public CatalogSnapshot Snapshot => sp.GetRequiredService<CatalogService>().LoadSnapshot();

        public bool OpenFolder(string path) => sp.GetRequiredService<WindowsPlatform>().Folders.Open(path);
    }

    /// <summary>Найденные Rhino (заполняется при запуске).</summary>
    internal sealed class TargetsHolder
    {
        public IReadOnlyList<RhinoTarget> Targets { get; set; } = [];
    }

    /// <summary>Progress без SynchronizationContext: значение уходит в обработчик сразу (UI-поток — через IUiDispatcher).</summary>
    private sealed class ImmediateProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    /// <summary>Возврат фокуса окну (FR-55a): пересканирование не чаще раза в 2 с.</summary>
    public void OnWindowActivated() => Get<ProfileWatchService>().OnActivated();

    private IDisposable? _hostsWatch;

    public void Dispose()
    {
        _hostsWatch?.Dispose();
        Get<ProfileWatchService>().Dispose();
        Get<IconLoader>().StopAsync().Wait(TimeSpan.FromSeconds(2));
        Get<IAppLog>().Info("YakUI завершает работу");
        _services.Dispose();
    }
}
