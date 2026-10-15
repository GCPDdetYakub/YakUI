using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YakUI.App.Resources;
using YakUI.App.Services;
using YakUI.Core.Api;
using YakUI.Core.Archive;
using YakUI.Core.Catalog;
using YakUI.Core.Compat;
using YakUI.Core.Localization;
using YakUI.Core.Versioning;

namespace YakUI.App.ViewModels;

/// <summary>Данные карточки пакета: версии (кэш), владельцы (сервер), инспекция .yak, открытие ссылок.</summary>
public interface IPackageCardSource
{
    Task<CatalogResult<IReadOnlyList<PackageVersionInfo>>> GetVersionsAsync(string name, CancellationToken ct);

    Task<IReadOnlyList<PackageOwner>> GetOwnersAsync(string name, CancellationToken ct);

    Task<YakInspection> InspectAsync(string url, string? filename, CancellationToken ct);

    bool OpenUrl(string? url);

    /// <summary>Поставить .yak в очередь загрузок (FR-36).</summary>
    void Download(YakUI.Core.Downloads.DownloadRequest request);

    /// <summary>Разбор архива уже есть в кэше (тогда наличие файла на сервере проверяется HEAD, FR-37a).</summary>
    bool IsInspectionCached(string url);

    /// <summary>FR-37a: один HEAD; результат запоминается на время работы.</summary>
    Task<YakUI.Core.Downloads.RemoteFileState> CheckRemoteAsync(string url, string? filename, CancellationToken ct);

    /// <summary>Запомнить наличие или удаление, установленное свежим разбором архива.</summary>
    void RecordRemote(string url, YakUI.Core.Downloads.RemoteFileState state, string? filename);

    /// <summary>Файл уже лежит в папке загрузок (установка из него разрешена и при удалении с сервера).</summary>
    bool IsDownloaded(string url);

    /// <summary>Выбранная версия Rhino установлена — в её стандартный профиль можно ставить (FR-81e).</summary>
    bool IsInstallableTarget(RhinoRelease? target);

    /// <summary>
    /// Стандартная установка (AD-03, FR-50) фоновой операцией: файл из загрузок или скачанный, затем yak цели
    /// или встроенный установщик; после успеха обновляет «Установленные» и метки каталога.
    /// </summary>
    Task<YakUI.Core.Install.InstallResult> InstallAsync(CardInstallRequest request, CancellationToken ct);

    /// <summary>
    /// «Установить» с выбором режима (FR-40, FR-45): стандартный профиль — <see cref="InstallAsync"/>, своя папка или
    /// «только распаковать» — пользовательский путь. null — пользователь отменил.
    /// </summary>
    Task<UninstallOutcome?> InstallWithOptionsAsync(CardInstallRequest request);

    /// <summary>«Удалить» пакет из цели целиком (FR-48): подтверждение, удаление, обновление экранов; итог или null при отказе.</summary>
    Task<UninstallOutcome?> UninstallAsync(string package, RhinoRelease target);

    /// <summary>Закреплена ли версия пакета в цели (FR-58).</summary>
    bool IsPinned(string package, RhinoRelease? target);

    /// <summary>Обновление или откат установленного пакета на выбранную версию (FR-49, FR-58).</summary>
    Task<UninstallOutcome?> ChangeVersionAsync(string package, string? from, string version, RhinoRelease target);

    /// <summary>Закрепить активную версию или снять закрепление (FR-58, ОВ-19).</summary>
    Task<UninstallOutcome?> TogglePinAsync(string package, string? version, RhinoRelease target);
}

/// <summary>Что ставит карточка: пакет, версия, выбранный по разделу 17 дистрибутив, цель.</summary>
public sealed record CardInstallRequest(string Package, string Version, PackageDistribution Distribution, DateTimeOffset? PublishedAt, RhinoRelease Target)
{
    /// <summary>Подпапки сред из инспекции архива (FR-31) — для окна установки (FR-43).</summary>
    public IReadOnlyList<string> Frameworks { get; init; } = [];
}

/// <summary>Метки статуса совместимости для привязки классов стиля.</summary>
public abstract class StatusRow : ObservableObject
{
    protected StatusRow(CompatibilityStatus? status)
    {
        Status = status;
        StatusText = status is { } s ? CatalogItemViewModel.StatusName(s) : null;
    }

    public CompatibilityStatus? Status { get; }

    public string? StatusText { get; }

    public bool IsNative => Status == CompatibilityStatus.Native;

    public bool IsBackward => Status == CompatibilityStatus.BackwardCompatible;

    public bool IsUnrestricted => Status == CompatibilityStatus.Unrestricted;

    public bool IsIncompatible => Status == CompatibilityStatus.Incompatible;

    public bool IsUnknown => Status == CompatibilityStatus.Unknown;
}

public sealed partial class DistributionRowViewModel : StatusRow
{
    /// <summary>FR-37a: сервер ответил 404/410 — файла больше нет.</summary>
    [ObservableProperty]
    private bool _isGone;

    public DistributionRowViewModel(PackageDistribution d, CompatibilityStatus? status, bool isBest)
        : base(status)
    {
        Distribution = d;
        Tag = d.Tag.Original;
        Filename = d.Filename ?? "";
        Size = d.Size is { } s ? FormatSize(s) : "";
        IsBest = isBest;
    }

    public PackageDistribution Distribution { get; }

    public string Tag { get; }

    public string Filename { get; }

    public string Size { get; }

    /// <summary>Этот дистрибутив будет выбран для установки по 17.6.</summary>
    public bool IsBest { get; }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => L.T("Size.B", $"{bytes} Б"),
        < 1024 * 1024 => L.T("Size.KB", $"{bytes / 1024.0:0.#} КБ"),
        < 1024L * 1024 * 1024 => L.T("Size.MB", $"{bytes / 1024.0 / 1024:0.#} МБ"),
        _ => L.T("Size.GB", $"{bytes / 1024.0 / 1024 / 1024:0.##} ГБ"),
    };
}

public sealed class VersionRowViewModel : StatusRow
{
    public VersionRowViewModel(PackageVersionInfo info, CompatibilityStatus? status, RhinoRelease? target)
        : base(status)
    {
        Info = info;
        Version = info.Version;
        Date = info.CreatedAt?.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.CurrentCulture) ?? "";
        IsPrerelease = info.Prerelease;
        Tags = string.Join(", ", info.Distributions.Select(d => d.Tag.Original));

        var best = target is { } t ? CompatibilityRules.SelectBest(info.Distributions, d => d.Tag, t) : null;
        Distributions = info.Distributions
            .Select(d => new DistributionRowViewModel(d, target is { } tr ? CompatibilityRules.Evaluate(d.Tag, tr).Status : null, ReferenceEquals(d, best)))
            .ToList();
        Best = best ?? info.Distributions.FirstOrDefault(d => d.Tag.Platform != DistributionPlatform.Mac);
    }

    public PackageVersionInfo Info { get; }

    public string Version { get; }

    public string Date { get; }

    public bool IsPrerelease { get; }

    public string Tags { get; }

    public IReadOnlyList<DistributionRowViewModel> Distributions { get; }

    /// <summary>Дистрибутив для инспекции и установки: лучший по 17.6, без цели — первый не-Mac.</summary>
    public PackageDistribution? Best { get; }
}

/// <summary>
/// Карточка пакета (FR-30, FR-31, FR-33): описание, авторы и владельцы, сайт, ключевые слова, загрузки,
/// даты, зависимости, все версии с дистрибутивами и статусом для цели, инспекция архива частичным чтением.
/// </summary>
public sealed partial class PackageCardViewModel : ObservableObject
{
    private readonly IPackageCardSource _source;
    private readonly IUiDispatcher _ui;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _inspectCts;
    private RhinoRelease? _target;

    public PackageCardViewModel(IPackageCardSource source, IUiDispatcher ui)
    {
        _source = source;
        _ui = ui;
    }

    [ObservableProperty]
    private CatalogItemViewModel? _item;

    [ObservableProperty]
    private ObservableCollection<VersionRowViewModel> _versions = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DistributionsTitle))]
    private VersionRowViewModel? _selectedVersion;

    [ObservableProperty]
    private string? _owners;

    [ObservableProperty]
    private string? _downloads;

    [ObservableProperty]
    private string? _dependencies;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _loadMessage;

    [ObservableProperty]
    private bool _isInspecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInspection), nameof(ContentKindText), nameof(FrameworksText), nameof(RhinoPluginsText),
        nameof(GhAssembliesText), nameof(GhUserObjectsText), nameof(GhDefinitionsText), nameof(ArchiveSizeText), nameof(UnsafeText), nameof(ManifestProblem))]
    private YakInspection? _inspection;

    [ObservableProperty]
    private string? _inspectionError;

    public bool HasHomepage => Item?.Entry.Package.HomepageUrl is not null;

    public string? HomepageUrl => Item?.Entry.Package.HomepageUrl;

    public string? DistributionsTitle => SelectedVersion is { } v ? string.Format(CultureInfo.CurrentCulture, Strings.Card_Distributions, v.Version) : null;

    public bool HasInspection => Inspection is not null;

    public string? ContentKindText => Inspection?.ContentKind switch
    {
        PackageContentKind.Rhino => Strings.Card_Kind_Rhino,
        PackageContentKind.Grasshopper => Strings.Card_Kind_Grasshopper,
        PackageContentKind.Mixed => Strings.Card_Kind_Mixed,
        PackageContentKind.None => Strings.Card_Kind_None,
        _ => null,
    };

    public string? FrameworksText => Inspection is { } i ? (i.Frameworks.Count == 0 ? "—" : string.Join(", ", i.Frameworks)) : null;

    public string? RhinoPluginsText => Inspection is { } i ? (i.RhinoPlugins.Count == 0 ? "—" : string.Join("\n", i.RhinoPlugins)) : null;

    public string? GhAssembliesText => Inspection is { } i ? (i.GrasshopperAssemblies.Count == 0 ? "—" : string.Join("\n", i.GrasshopperAssemblies)) : null;

    /// <summary>FR-31: пользовательские объекты <c>.ghuser</c> — числом, до 5 — с именами.</summary>
    public string? GhUserObjectsText => Inspection is { } i ? CountText(i.GrasshopperUserObjects) : null;

    /// <summary>FR-31: определения <c>.gh</c> / <c>.ghx</c>.</summary>
    public string? GhDefinitionsText => Inspection is { } i ? CountText(i.GrasshopperDefinitions) : null;

    internal static string CountText(IReadOnlyList<string> files) => files.Count switch
    {
        0 => "—",
        <= 5 => string.Join("\n", files),
        _ => string.Format(CultureInfo.CurrentCulture, Strings.Card_FilesCount, files.Count),
    };

    public string? ArchiveSizeText => Inspection is { } i
        ? string.Format(CultureInfo.CurrentCulture, Strings.Card_ArchiveSizeValue,
            DistributionRowViewModel.FormatSize(i.ArchiveSize), DistributionRowViewModel.FormatSize(i.UncompressedSize), DistributionRowViewModel.FormatSize(i.BytesRead))
        : null;

    public string? UnsafeText => Inspection is { UnsafePaths.Count: > 0 } i
        ? string.Format(CultureInfo.CurrentCulture, Strings.Card_UnsafePaths, string.Join(", ", i.UnsafePaths))
        : null;

    public string? ManifestProblem => Inspection is { ManifestError: { } e } ? string.Format(CultureInfo.CurrentCulture, Strings.Card_ManifestMissing, e) : null;

    /// <summary>Последняя запущенная загрузка (для тестов).</summary>
    internal Task LastLoad { get; private set; } = Task.CompletedTask;

    internal Task LastInspection { get; private set; } = Task.CompletedTask;

    /// <summary>Показать пакет для цели; повторный вызов с тем же пакетом пересчитывает статусы.</summary>
    public void Show(CatalogItemViewModel? item, RhinoRelease? target)
    {
        _target = target;
        _loadCts?.Cancel();
        Item = item;
        OnPropertyChanged(nameof(HasHomepage));
        OnPropertyChanged(nameof(HomepageUrl));
        Versions = [];
        SelectedVersion = null;
        Owners = null;
        Downloads = null;
        Dependencies = null;
        LoadMessage = null;
        DownloadMessage = null;
        InstallMessage = null;
        _uninstalled = false;
        InstallCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        ChangeVersionCommand.NotifyCanExecuteChanged();
        TogglePinCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(PinButtonText));
        if (item is null)
            return;

        var cts = _loadCts = new CancellationTokenSource();
        var name = item.Name;
        IsLoading = true;
        LastLoad = Task.Run(async () =>
        {
            CatalogResult<IReadOnlyList<PackageVersionInfo>>? versions = null;
            string? error = null;
            try
            {
                versions = await _source.GetVersionsAsync(name, cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                error = e.Message;
            }

            string owners;
            try
            {
                var list = await _source.GetOwnersAsync(name, cts.Token).ConfigureAwait(false);
                owners = list.Count == 0 ? "—" : string.Join(", ", list.Select(o => o.Name));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                owners = Strings.Card_OwnersUnavailable;
            }

            cts.Token.ThrowIfCancellationRequested();
            _ui.Post(() =>
            {
                if (cts.IsCancellationRequested)
                    return;
                IsLoading = false;
                Owners = owners;
                if (versions is null)
                {
                    LoadMessage = string.Format(CultureInfo.CurrentCulture, Strings.Card_LoadFailed, error);
                    return;
                }

                if (versions.IsOffline)
                    LoadMessage = Strings.Card_Offline;
                Apply(versions.Value, item);
            });
        }, cts.Token);
    }

    private void Apply(IReadOnlyList<PackageVersionInfo> versions, CatalogItemViewModel item)
    {
        var rows = versions
            .OrderByDescending(v => PackageVersion.TryParse(v.Version, out var pv) ? pv : null, Comparer<PackageVersion?>.Create((a, b) => a is null ? (b is null ? 0 : -1) : a.CompareTo(b)))
            .Select(v => new VersionRowViewModel(v, _target is { } t ? CompatibilityRules.EvaluateVersion(v.Distributions.Select(d => d.Tag), t) : null, _target))
            .ToList();
        Versions = new ObservableCollection<VersionRowViewModel>(rows);

        var latest = rows.FirstOrDefault()?.Info;
        var d = latest?.Downloads;
        Downloads = string.Format(CultureInfo.CurrentCulture, Strings.Card_DownloadsValue,
            item.Entry.Package.DownloadCount.ToString("N0", CultureInfo.CurrentCulture),
            (d?.LastDay ?? 0).ToString("N0", CultureInfo.CurrentCulture),
            (d?.LastWeek ?? 0).ToString("N0", CultureInfo.CurrentCulture),
            item.Entry.Package.DownloadsLastMonth.ToString("N0", CultureInfo.CurrentCulture));
        Dependencies = FormatDependencies(latest?.Dependencies);

        // По умолчанию — «версия для цели» из каталога.
        SelectedVersion = rows.FirstOrDefault(r => r.Version == item.Entry.TargetVersion) ?? rows.FirstOrDefault();
    }

    partial void OnSelectedVersionChanged(VersionRowViewModel? value)
    {
        IsSelectedDownloaded = value?.Best?.Url is { } u && _source.IsDownloaded(u);
        UpdateAvailability();
        Inspect(value?.Best);
    }

    /// <summary>Инспекция выбранного дистрибутива частичным чтением (FR-31, FR-36b); результат кэшируется сервисом.</summary>
    private void Inspect(PackageDistribution? distribution)
    {
        _inspectCts?.Cancel();
        Inspection = null;
        InspectionError = null;
        if (distribution?.Url is not { } url)
        {
            IsInspecting = false;
            return;
        }

        var cts = _inspectCts = new CancellationTokenSource();
        IsInspecting = true;
        var row = SelectedVersion?.Distributions.FirstOrDefault(r => ReferenceEquals(r.Distribution, distribution));
        LastInspection = Task.Run(async () =>
        {
            YakInspection? result = null;
            string? error = null;
            var gone = false;
            var fromCache = _source.IsInspectionCached(url);
            try
            {
                result = await _source.InspectAsync(url, distribution.Filename, cts.Token).ConfigureAwait(false);
                if (fromCache)
                {
                    // FR-37a: разбор из кэша не говорит, жив ли файл, — один HEAD.
                    gone = await _source.CheckRemoteAsync(url, distribution.Filename, cts.Token).ConfigureAwait(false) == YakUI.Core.Downloads.RemoteFileState.Gone;
                }
                else
                {
                    // Свежий разбор уже прочитал файл по Range — значит, он есть.
                    _source.RecordRemote(url, YakUI.Core.Downloads.RemoteFileState.Exists, distribution.Filename);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return;
            }
            catch (HttpRequestException e) when (e.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
            {
                gone = true;
                _source.RecordRemote(url, YakUI.Core.Downloads.RemoteFileState.Gone, distribution.Filename);
            }
            catch (Exception e)
            {
                error = e.Message;
            }

            var downloaded = _source.IsDownloaded(url);
            _ui.Post(() =>
            {
                if (cts.IsCancellationRequested)
                    return;
                IsInspecting = false;
                Inspection = result;
                InspectionError = gone ? Strings.Card_FileGone : error is null ? null : string.Format(CultureInfo.CurrentCulture, Strings.Card_InspectFailed, error);
                if (row is not null)
                    row.IsGone = gone;
                IsSelectedDownloaded = downloaded;
                UpdateAvailability();
            });
        });
    }

    [ObservableProperty]
    private string? _downloadMessage;

    /// <summary>Дистрибутив выбранной версии, который будет установлен, удалён с сервера (FR-37a).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadSelectedCommand), nameof(DownloadAllCommand))]
    private bool _isSelectedGone;

    /// <summary>Файл выбранного дистрибутива уже в папке загрузок.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallFromDownloads))]
    private bool _isSelectedDownloaded;

    /// <summary>
    /// FR-37a: установка выбранного дистрибутива возможна — файл на сервере есть (или не проверен),
    /// либо удалён, но уже скачан. Кнопка «Установить» появится на шаге 2.9.
    /// </summary>
    public bool CanInstall => SelectedVersion?.Best is not null && (!IsSelectedGone || IsSelectedDownloaded);

    /// <summary>
    /// «Установить»: цель — установленный Rhino (FR-81e), дистрибутив выбран и не несовместим/не Mac-only
    /// (FR-23, раздел 17), файл доступен (FR-37a), другая установка не идёт.
    /// </summary>
    private bool CanInstallSelected() =>
        !IsInstalling && !_uninstalled && Item?.Entry.InstalledVersion is null && CanInstall && _source.IsInstallableTarget(_target)
        && SelectedVersion?.Distributions.FirstOrDefault(r => ReferenceEquals(r.Distribution, SelectedVersion.Best)) is { IsIncompatible: false, IsUnknown: false };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand), nameof(ChangeVersionCommand), nameof(TogglePinCommand))]
    private bool _isInstalling;

    /// <summary>
    /// «Сменить на эту версию» (FR-49): пакет установлен в цель, выбрана другая версия с подходящим дистрибутивом.
    /// </summary>
    private bool CanChangeVersion() =>
        !IsInstalling && !_uninstalled && Item?.Entry.InstalledVersion is { } installed && SelectedVersion is { } v
        && !YakUI.Core.Install.BuiltInInstaller.SameVersion(installed, v.Version)
        && CanInstall && _source.IsInstallableTarget(_target)
        && v.Distributions.FirstOrDefault(r => ReferenceEquals(r.Distribution, v.Best)) is { IsIncompatible: false, IsUnknown: false };

    [RelayCommand(CanExecute = nameof(CanChangeVersion))]
    private async Task ChangeVersion()
    {
        if (Item is not { } item || SelectedVersion is not { } v || _target is not { } target)
            return;
        IsInstalling = true;
        try
        {
            if (await _source.ChangeVersionAsync(item.Name, item.Entry.InstalledVersion, v.Version, target) is { } outcome)
            {
                InstallMessage = outcome.Message;
                _uninstalled = outcome.Succeeded; // метки каталога обновятся после пересканирования
            }
        }
        finally
        {
            IsInstalling = false;
            ChangeVersionCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsPinned => Item is { } i && _source.IsPinned(i.Name, _target);

    public string PinButtonText => IsPinned ? Strings.Card_Unpin : Strings.Card_Pin;

    private bool CanTogglePin() => !IsInstalling && !_uninstalled && Item?.Entry.InstalledVersion is not null && _source.IsInstallableTarget(_target);

    [RelayCommand(CanExecute = nameof(CanTogglePin))]
    private async Task TogglePin()
    {
        if (Item is not { } item || _target is not { } target)
            return;
        if (await _source.TogglePinAsync(item.Name, item.Entry.InstalledVersion, target) is { } outcome)
            InstallMessage = outcome.Message;
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(PinButtonText));
    }

    [ObservableProperty]
    private string? _installMessage;

    /// <summary>Пакет удалён из цели в этой карточке (метки каталога обновятся после пересканирования).</summary>
    private bool _uninstalled;

    /// <summary>«Удалить»: пакет установлен в выбранную цель — установленный Rhino (FR-48, FR-81e).</summary>
    private bool CanUninstall() =>
        !IsInstalling && !_uninstalled && Item?.Entry.InstalledVersion is not null && _source.IsInstallableTarget(_target);

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task Uninstall()
    {
        if (Item is not { } item || _target is not { } target)
            return;
        IsInstalling = true;
        try
        {
            if (await _source.UninstallAsync(item.Name, target) is { } outcome)
            {
                InstallMessage = outcome.Message;
                _uninstalled = outcome.Succeeded;
            }
        }
        finally
        {
            IsInstalling = false;
            UninstallCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Установка в профиль текущего пользователя выбранной в списке целей версии Rhino (FR-40).</summary>
    [RelayCommand(CanExecute = nameof(CanInstallSelected))]
    private async Task Install()
    {
        if (Item is not { } item || SelectedVersion is not { Best: { } best } v || _target is not { } target)
            return;

        IsInstalling = true;
        InstallMessage = string.Format(CultureInfo.CurrentCulture, Strings.Card_Installing, item.Name, v.Version);
        try
        {
            // Окно режима (FR-40, FR-45): стандартный профиль, своя папка или «только распаковать»; null — отмена.
            var outcome = await _source.InstallWithOptionsAsync(new CardInstallRequest(item.Name, v.Version, best, v.Info.CreatedAt, target)
            {
                Frameworks = Inspection?.Frameworks ?? [],
            });
            InstallMessage = outcome?.Message;
            if (outcome is { Succeeded: true })
                _uninstalled = true; // метки каталога обновятся после пересканирования
        }
        catch (Exception e)
        {
            InstallMessage = string.Format(CultureInfo.CurrentCulture, Strings.Card_InstallFailed, e.Message);
        }
        finally
        {
            IsInstalling = false;
        }
    }

    /// <summary>Итог стандартной установки одной строкой на пункт (способ, предупреждения, перезапуск).</summary>
    public static string FormatInstall(YakUI.Core.Install.InstallResult r)
    {
        var lines = new List<string>
        {
            string.Format(CultureInfo.CurrentCulture, Strings.Card_Installed, r.Name, r.Version,
                r.Method == YakUI.Core.Install.InstallMethod.Yak ? Strings.Card_InstallMethodYak : Strings.Card_InstallMethodBuiltIn),
        };
        lines.AddRange(r.Warnings);
        if (r.PendingRestart)
            lines.Add(Strings.Card_InstallRestart);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Файл удалён с сервера, но есть в загрузках — установка только из него.</summary>
    public bool CanInstallFromDownloads => IsSelectedGone && IsSelectedDownloaded;

    private void UpdateAvailability()
    {
        IsSelectedGone = SelectedVersion is { Best: { } best } v && v.Distributions.Any(r => ReferenceEquals(r.Distribution, best) && r.IsGone);
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanInstallFromDownloads));
        DownloadSelectedCommand.NotifyCanExecuteChanged();
        DownloadAllCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
        ChangeVersionCommand.NotifyCanExecuteChanged();
    }

    private bool CanDownloadSelected() => SelectedVersion?.Best is not null && !IsSelectedGone;

    private bool CanDownloadAll() => SelectedVersion?.Distributions.Any(r => !r.IsGone) == true;

    /// <summary>FR-36: скачать дистрибутив выбранной версии, который будет установлен (или первый не-Mac).</summary>
    [RelayCommand(CanExecute = nameof(CanDownloadSelected))]
    private void DownloadSelected()
    {
        if (SelectedVersion?.Best is { } d)
            Enqueue([d]);
        else
            DownloadMessage = Strings.Card_NothingToDownload;
    }

    /// <summary>FR-36: все дистрибутивы выбранной версии, кроме удалённых с сервера (FR-37a).</summary>
    [RelayCommand(CanExecute = nameof(CanDownloadAll))]
    private void DownloadAll()
    {
        if (SelectedVersion is { } v && v.Distributions.Any(r => !r.IsGone))
            Enqueue(v.Distributions.Where(r => !r.IsGone).Select(r => r.Distribution).ToList());
        else
            DownloadMessage = Strings.Card_NothingToDownload;
    }

    private void Enqueue(IReadOnlyList<PackageDistribution> distributions)
    {
        var names = new List<string>();
        foreach (var d in distributions.Where(d => d.Url is not null))
        {
            var file = d.Filename ?? Path.GetFileName(new Uri(d.Url!).LocalPath);
            _source.Download(new YakUI.Core.Downloads.DownloadRequest(d.Url!, file, Item?.Name, SelectedVersion?.Version, d.Tag.Original, d.Size, SelectedVersion?.Info.CreatedAt));
            names.Add(file);
        }

        DownloadMessage = names.Count == 0 ? Strings.Card_NothingToDownload : string.Format(CultureInfo.CurrentCulture, Strings.Card_DownloadQueued, string.Join(", ", names));
    }

    [RelayCommand]
    private void OpenHomepage() => _source.OpenUrl(HomepageUrl);

    /// <summary>FR-100: поиск Food4Rhino по имени пакета в системном браузере.</summary>
    [RelayCommand]
    private void SearchFood4Rhino() =>
        _source.OpenUrl(Item is { } item ? Food4RhinoSearchUrl(item.Name) : Food4RhinoHome);

    /// <summary>Главная Food4Rhino — пакет в каталоге не выбран.</summary>
    internal const string Food4RhinoHome = "https://www.food4rhino.com/";

    internal static string Food4RhinoSearchUrl(string packageName) =>
        "https://www.food4rhino.com/en/search?search_api_fulltext=" + Uri.EscapeDataString(packageName);

    internal static string FormatDependencies(JsonElement? deps)
    {
        if (deps is not { } d || d.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return Strings.Card_NoDependencies;
        if (d.ValueKind == JsonValueKind.Array)
        {
            var items = d.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            return items.Count == 0 ? Strings.Card_NoDependencies : string.Join(", ", items);
        }

        if (d.ValueKind == JsonValueKind.Object)
        {
            var items = d.EnumerateObject().Select(p => $"{p.Name} {p.Value}").ToList();
            return items.Count == 0 ? Strings.Card_NoDependencies : string.Join(", ", items);
        }

        return d.ToString();
    }
}
