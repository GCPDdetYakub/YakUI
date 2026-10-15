using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YakUI.App.Resources;
using YakUI.App.Services;
using YakUI.Core.Archive;
using YakUI.Core.Catalog;
using YakUI.Core.Compat;
using YakUI.Core.Icons;
using YakUI.Core.Install;
using YakUI.Core.Targets;

namespace YakUI.App.ViewModels;

/// <summary>Данные экрана «Установленные».</summary>
public interface IInstalledSource
{
    IReadOnlyList<RhinoTarget> Targets { get; }

    Task<InstalledOverview> GetAsync(RhinoTarget target, CancellationToken ct);

    /// <summary>Текущий снимок каталога (для «обновление → X» и «показать в каталоге»).</summary>
    CatalogSnapshot Snapshot { get; }

    bool OpenFolder(string path);
}

/// <summary>Строка списка установленного (FR-56).</summary>
public sealed partial class InstalledRowViewModel : ObservableObject
{
    public InstalledRowViewModel(InstalledItem item, CatalogMatch match, PackageIconViewModel icon, bool showTarget, PinRecord? pin = null)
    {
        Pin = pin;
        PinnedText = pin is null ? null
            : item.ActiveVersion is { } active && !BuiltInInstaller.SameVersion(active, pin.Version)
                ? string.Format(CultureInfo.CurrentCulture, Strings.Lc_PinStale, pin.Version, active)
                : string.Format(CultureInfo.CurrentCulture, Strings.Lc_PinMark, pin.Version);
        UpdateTo = match.UpdateTo;
        Item = item;
        Icon = icon;
        Name = item.Name;
        Version = item.ActiveVersion ?? "?";
        InactiveText = item.InactiveVersions.Count > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.Inst_Inactive, string.Join(", ", item.InactiveVersions)) : null;
        ModeText = item.Scope switch
        {
            InstalledScope.Custom => Strings.Inst_ModeCustom,
            InstalledScope.Unpacked => Strings.Inst_ModeUnpack,
            _ => Strings.Inst_ModeStandard,
        };
        SourceText = item.CustomRecord is { } rec
            ? rec.Source switch
            {
                InstallSource.Food4Rhino => Strings.Inst_ImportF4R,
                InstallSource.File => Strings.Inst_ImportFile,
                _ => string.Format(CultureInfo.CurrentCulture, Strings.Inst_SourceCustom, rec.Root),
            }
            : item.IsReadOnly ? Strings.Inst_SourceAllUsers : Strings.Inst_SourceUser;
        DuplicateText = item.IsDuplicate ? Strings.Inst_Duplicate : null;
        KindText = item.ContentKind switch
        {
            PackageContentKind.Rhino => Strings.Card_Kind_Rhino,
            PackageContentKind.Grasshopper => Strings.Card_Kind_Grasshopper,
            PackageContentKind.Mixed => Strings.Card_Kind_Mixed,
            _ => Strings.Card_Kind_None,
        };
        StatusText = item.Status switch
        {
            InstalledStatus.PendingRestart => Strings.Inst_StatusPendingRestart,
            InstalledStatus.Unknown => Strings.Inst_StatusUnknown,
            _ => Strings.Inst_StatusActive,
        };
        IsPendingRestart = item.Status == InstalledStatus.PendingRestart;
        DiscrepancyText = item.Discrepancy switch
        {
            DiscrepancyKind.MissingInYakList => Strings.Inst_MissingInYakList,
            DiscrepancyKind.MissingInProfile => string.Format(CultureInfo.CurrentCulture, Strings.Inst_MissingInProfile, item.YakListVersion),
            DiscrepancyKind.VersionMismatch => string.Format(CultureInfo.CurrentCulture, Strings.Inst_VersionMismatch, item.YakListVersion),
            _ => null,
        };
        InCatalog = match.InCatalog;
        UpdateText = match.UpdateTo is { } u ? string.Format(CultureInfo.CurrentCulture, Strings.Cat_UpdateTo, u) : null;
        NotInCatalogText = match.InCatalog ? null : Strings.Inst_NotInCatalog;
        TargetTitle = showTarget ? item.Target.DisplayName : null;
        Path = item.VersionDirectory ?? item.PackageDirectory;
        Tooltip = InstalledOverviewService.Describe(item);
    }

    public InstalledItem Item { get; }

    public PackageIconViewModel Icon { get; }

    public string Name { get; }

    public string Version { get; }

    public string? InactiveText { get; }

    public string ModeText { get; }

    public string SourceText { get; }

    public bool IsReadOnly => Item.IsReadOnly;

    public string KindText { get; }

    public string StatusText { get; }

    public bool IsPendingRestart { get; }

    public string? DiscrepancyText { get; }

    public bool HasDiscrepancy => DiscrepancyText is not null;

    public bool InCatalog { get; }

    public string? UpdateText { get; }

    public string? NotInCatalogText { get; }

    /// <summary>Цель — только в сводке «Любая версия».</summary>
    public string? TargetTitle { get; }

    public string Path { get; }

    public string Tooltip { get; }

    /// <summary>Закреплённая версия (FR-58).</summary>
    public PinRecord? Pin { get; }

    public bool IsPinned => Pin is not null;

    /// <summary>«закреплена X» или «закреплена X, активна Y — изменено вне YakUI» (ОВ-19).</summary>
    public string? PinnedText { get; }

    public bool IsPinStale => Pin is not null && Item.ActiveVersion is { } a && !BuiltInInstaller.SameVersion(a, Pin.Version);

    public string? UpdateTo { get; }

    /// <summary>Пакет профиля пользователя, который можно менять (не «для всех», есть на диске).</summary>
    /// <summary>Пакет стандартного профиля пользователя, который есть на диске: массовые действия и закрепление (этап 4).</summary>
    public bool IsEditable => Item.Scope == InstalledScope.User && Item.Discrepancy != DiscrepancyKind.MissingInProfile;

    /// <summary>Установка по пользовательскому пути или «только распаковать» (этап 3, FR-46).</summary>
    public bool IsCustom => Item.CustomRecord is not null;

    /// <summary>FR-47: и в профиле, и по пользовательскому пути.</summary>
    public string? DuplicateText { get; }

    public bool HasDuplicate => DuplicateText is not null;

    /// <summary>Галочка выборочных действий (FR-59).</summary>
    [ObservableProperty]
    private bool _isChecked;
}

/// <summary>
/// Экран «Установленные» (11, FR-55…FR-58, FR-05). Для установленной цели — её профиль пользователя, профиль «для всех»
/// (только чтение) и сверка с <c>yak list</c>; для «Любой версии» — сводка по всем целям (FR-81e); для неустановленной —
/// пояснение. Действия: открыть папку, показать в каталоге, удалить (FR-48).
/// </summary>
public sealed partial class InstalledViewModel : ScreenViewModel
{
    private readonly IInstalledSource _source;
    private readonly TargetSelectorViewModel _target;
    private readonly IIconProvider _icons;
    private readonly IUiDispatcher _ui;
    private readonly UninstallCoordinator? _uninstall;
    private readonly LifecycleCoordinator? _lifecycle;
    private readonly CustomPathService? _paths;
    private readonly Func<Task<string?>>? _pickFile;
    private readonly Func<Task<string?>>? _pickFolder;
    private CancellationTokenSource? _cts;
    private List<InstalledRowViewModel> _all = [];

    public InstalledViewModel(IInstalledSource source, TargetSelectorViewModel target, IIconProvider icons, IUiDispatcher ui,
        UninstallCoordinator? uninstall = null, LifecycleCoordinator? lifecycle = null, CustomPathService? paths = null, Func<Task<string?>>? pickFile = null,
        Func<Task<string?>>? pickFolder = null)
        : base(ScreenKind.Installed, Strings.Nav_Installed)
    {
        _pickFolder = pickFolder;
        _paths = paths;
        _pickFile = pickFile;
        _uninstall = uninstall;
        _lifecycle = lifecycle;
        _source = source;
        _target = target;
        _icons = icons;
        _ui = ui;
        _target.PropertyChanged += OnTargetChanged;
    }

    /// <summary>«Показать в каталоге» (FR-58): имя пакета.</summary>
    public event Action<string>? ShowInCatalogRequested;

    [ObservableProperty]
    private ObservableCollection<InstalledRowViewModel> _rows = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand), nameof(ShowInCatalogCommand), nameof(UninstallCommand),
        nameof(UpdateCommand), nameof(TogglePinCommand))]
    private InstalledRowViewModel? _selectedRow;

    /// <summary>ОВ-19: Rhino цели сам обновляет пакеты при запуске.</summary>
    [ObservableProperty]
    private string? _autoUpdateText;

    /// <summary>Итог последнего удаления.</summary>
    [ObservableProperty]
    private string? _actionMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand), nameof(UpdateCommand), nameof(TogglePinCommand), nameof(UpdateAllCommand),
        nameof(UpdateCheckedCommand), nameof(UninstallCheckedCommand))]
    private bool _isUninstalling;

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string? _runningText;

    [ObservableProperty]
    private string? _yakListProblem;

    [ObservableProperty]
    private string _countText = "";

    [ObservableProperty]
    private bool _showTargetColumn;

    internal Task LastRefresh { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    public void Refresh()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var choice = _target.Current;
        Rows = [];
        _all = [];
        Message = null;
        RunningText = null;
        YakListProblem = null;
        AutoUpdateText = null;
        CountText = "";

        if (choice is null || choice.Kind == TargetChoiceKind.NotInstalled)
        {
            ShowTargetColumn = false;
            Message = choice is null ? null : string.Format(CultureInfo.CurrentCulture, Strings.Inst_NotInstalledTarget, choice.Major);
            LastRefresh = Task.CompletedTask;
            return;
        }

        var targets = choice.Kind == TargetChoiceKind.Installed ? [choice.Target!] : _source.Targets.ToList();
        var summary = choice.Kind == TargetChoiceKind.AnyVersion;
        ShowTargetColumn = summary;
        IsLoading = true;

        LastRefresh = Task.Run(async () =>
        {
            var overviews = new List<InstalledOverview>();
            foreach (var t in targets)
                overviews.Add(await _source.GetAsync(t, cts.Token).ConfigureAwait(false));

            var snapshot = _source.Snapshot;
            var pins = targets.ToDictionary(t => t, t => _lifecycle?.Backend.Pins(t) ?? new Dictionary<string, PinRecord>());
            var rows = overviews.SelectMany(o => o.Items.Select(i => BuildRow(i, snapshot, summary,
                i.Scope == InstalledScope.User ? pins[o.Target].GetValueOrDefault(i.Name) : null))).ToList();
            var autoUpdate = _lifecycle is null ? [] : targets.Select(_lifecycle.AutoUpdateText).OfType<string>().ToList();
            var running = overviews.Where(o => o.Running.Count > 0).Select(o => FormatRunning(o)).ToList();
            var problems = overviews.Where(o => o.YakListProblem is not null).Select(o => o.YakListProblem!).Distinct().ToList();

            _ui.Post(() =>
            {
                if (cts.IsCancellationRequested)
                    return;
                IsLoading = false;
                _all = rows;
                RunningText = running.Count == 0 ? null : string.Join("\n", running);
                AutoUpdateText = autoUpdate.Count == 0 ? null : string.Join("\n", autoUpdate);
                YakListProblem = problems.Count == 0 ? null : string.Format(CultureInfo.CurrentCulture, Strings.Inst_YakListProblem, string.Join(" ", problems));
                ApplyFilter();
            });
        }, cts.Token);
    }

    private InstalledRowViewModel BuildRow(InstalledItem item, CatalogSnapshot snapshot, bool summary, PinRecord? pin)
    {
        var match = InstalledOverviewService.MatchCatalog(snapshot, item.Name, item.ActiveVersion, item.Target.Release);
        var catalogRequest = snapshot.ByName.TryGetValue(item.Name, out var package) ? IconRequests.For(package) : null;
        var icon = _icons.ForInstalled(new InstalledIconRequest(
            item.Name,
            item.ActiveVersion ?? "",
            item.VersionDirectory,
            item.Manifest?.Icon,
            item.Manifest?.IconUrl,
            catalogRequest));
        return new InstalledRowViewModel(item, match, icon, summary, pin);
    }

    private static string FormatRunning(InstalledOverview o)
    {
        var who = string.Join(", ", o.Running.Select(r => r.IsInsideHost
            ? string.Format(CultureInfo.CurrentCulture, Strings.Inst_RunningInside, r.ProcessName)
            : string.Create(CultureInfo.InvariantCulture, $"PID {r.ProcessId}")));
        return string.Format(CultureInfo.CurrentCulture, Strings.Inst_Running, o.Target.DisplayName, who);
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var f = FilterText.Trim();
        var visible = f.Length == 0 ? _all : _all.Where(r => r.Name.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        var selected = SelectedRow?.Name;
        Rows = new ObservableCollection<InstalledRowViewModel>(visible);
        SelectedRow = selected is null ? null : Rows.FirstOrDefault(r => r.Name == selected);
        var updates = visible.Count(r => r.UpdateText is not null);
        UpdateAllCommand.NotifyCanExecuteChanged();
        CountText = updates > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.Inst_CountWithUpdates, visible.Count, updates)
            : string.Format(CultureInfo.CurrentCulture, Strings.Inst_Count, visible.Count);
        Message = visible.Count == 0 && !IsLoading ? Strings.Inst_Empty : null;
    }

    private bool HasSelection() => SelectedRow is not null;

    /// <summary>
    /// Удалить можно пакет из профиля пользователя, который есть на диске; профиль «для всех» — только чтение (FR-48).
    /// </summary>
    private bool CanUninstall() =>
        !IsUninstalling && SelectedRow is { } row
        && (row.IsCustom ? _paths is not null : _uninstall is not null && row.IsEditable);

    /// <summary>«Удалить» — пакет из цели строки целиком (FR-48, FR-85).</summary>
    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task Uninstall()
    {
        if (SelectedRow is not { } row)
            return;
        IsUninstalling = true;
        try
        {
            var outcome = row.Item.CustomRecord is { } rec
                ? await _paths!.UninstallAsync(row.Item.Target, rec)
                : await _uninstall!.UninstallAsync(row.Item.Target, row.Name);
            if (outcome is not null)
                ActionMessage = outcome.Message;
        }
        finally
        {
            IsUninstalling = false;
        }
    }

    // ---- Этап 4: обновление, откат, закрепление, массовые действия (FR-49, FR-58, FR-59) ----

    private bool Busy => IsUninstalling;

    private bool CanUpdate() => !Busy && SelectedRow is { UpdateTo: not null } row
        && (row.IsCustom ? _paths is not null && row.Item.CustomRecord!.Source == InstallSource.Catalog : _lifecycle is not null && row.IsEditable);

    /// <summary>«Обновить» выбранный пакет до самой новой подходящей версии (FR-49; по пользовательскому пути — тоже).</summary>
    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private Task Update() => RunAsync(row => row.Item.CustomRecord is { } rec
        ? _paths!.ChangeVersionAsync(row.Item.Target, rec, null)
        : _lifecycle!.ChangeVersionAsync(row.Item.Target, row.Name, row.Item.ActiveVersion, null));

    private bool CanImport() => _paths is not null && _pickFile is not null && !Busy;

    /// <summary>«Добавить файл…» (FR-101): импорт для выбранной установленной версии Rhino.</summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportFile()
    {
        if (await _pickFile!() is { } file)
            await ImportAsync([file]);
    }

    private bool CanImportFolder() => _paths is not null && _pickFolder is not null && !Busy;

    /// <summary>«Добавить папку…» — пакет целиком (13a).</summary>
    [RelayCommand(CanExecute = nameof(CanImportFolder))]
    private async Task ImportFolder()
    {
        if (await _pickFolder!() is { } dir)
            await ImportAsync([dir]);
    }

    /// <summary>Импорт файлов (кнопка или перетаскивание в окно, FR-101).</summary>
    public async Task ImportAsync(IEnumerable<string> files)
    {
        if (_paths is null)
            return;
        IsUninstalling = true;
        var lines = new List<string>();
        try
        {
            foreach (var f in files)
            {
                if (await _paths.ImportAsync(f, SingleTarget) is { } outcome)
                    lines.Add(outcome.Message);
            }
        }
        finally
        {
            IsUninstalling = false;
        }

        if (lines.Count > 0)
            ActionMessage = string.Join(Environment.NewLine, lines);
        Refresh();
    }

    private bool CanTogglePin() => _lifecycle is not null && !Busy && SelectedRow is { IsEditable: true };

    /// <summary>«Закрепить версию» / «Открепить» (FR-58; ОВ-19 — предупреждение).</summary>
    [RelayCommand(CanExecute = nameof(CanTogglePin))]
    private Task TogglePin() => RunAsync(row => row.IsPinned
        ? Task.FromResult<UninstallOutcome?>(_lifecycle!.Unpin(row.Item.Target, row.Name))
        : _lifecycle!.PinAsync(row.Item.Target, row.Name, row.Item.ActiveVersion));

    /// <summary>Одна цель на экране (не «Любая версия»): массовые действия идут по ней.</summary>
    private RhinoTarget? SingleTarget => _target.Current is { Kind: TargetChoiceKind.Installed, Target: { } t } ? t : null;

    private bool CanUpdateAll() => _lifecycle is not null && !Busy && SingleTarget is not null && _all.Any(r => r.IsEditable && r.UpdateTo is not null);

    /// <summary>«Обновить всё» (FR-59): все, кроме закреплённых.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private Task UpdateAll() => BulkUpdate(_all.Where(r => r.IsEditable && r.UpdateTo is not null).ToList(), selective: false);

    private bool CanBulkChecked() => !Busy && SingleTarget is not null;

    [RelayCommand(CanExecute = nameof(CanBulkChecked))]
    private Task UpdateChecked() => BulkUpdate(_all.Where(r => r.IsChecked && r.IsEditable && r.UpdateTo is not null && !r.IsPinned).ToList(), selective: true);

    /// <summary>Удалить отмеченные (FR-59): одно подтверждение с перечнем.</summary>
    [RelayCommand(CanExecute = nameof(CanBulkChecked))]
    private async Task UninstallChecked()
    {
        var rows = _all.Where(r => r.IsChecked && r.IsEditable).ToList();
        if (_uninstall is null || SingleTarget is not { } target)
            return;
        if (rows.Count == 0)
        {
            ActionMessage = Strings.Lc_NothingChecked;
            return;
        }

        IsUninstalling = true;
        try
        {
            if (await _uninstall.UninstallManyAsync(target, rows.Select(r => r.Name).ToList()) is { } outcome)
                ActionMessage = outcome.Message;
        }
        finally
        {
            IsUninstalling = false;
        }
    }

    private async Task BulkUpdate(List<InstalledRowViewModel> rows, bool selective)
    {
        if (_lifecycle is null || SingleTarget is not { } target)
            return;
        if (selective && rows.Count == 0)
        {
            ActionMessage = Strings.Lc_NothingChecked;
            return;
        }

        IsUninstalling = true;
        try
        {
            var plan = rows.Select(r => (r.Name, r.Item.ActiveVersion ?? "?", r.UpdateTo!)).ToList();
            if (await _lifecycle.UpdateAllAsync(target, plan, selective) is { } outcome)
                ActionMessage = outcome.Message;
        }
        finally
        {
            IsUninstalling = false;
        }
    }

    private async Task RunAsync(Func<InstalledRowViewModel, Task<UninstallOutcome?>> action)
    {
        if (SelectedRow is not { } row)
            return;
        IsUninstalling = true;
        try
        {
            if (await action(row) is { } outcome)
                ActionMessage = outcome.Message;
        }
        finally
        {
            IsUninstalling = false;
            Refresh();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenFolder()
    {
        if (SelectedRow is { } row)
            _source.OpenFolder(row.Path);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ShowInCatalog()
    {
        if (SelectedRow is { } row)
            ShowInCatalogRequested?.Invoke(row.Name);
    }

    private void OnTargetChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TargetSelectorViewModel.SelectedItem))
            Refresh();
    }
}
