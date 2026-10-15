using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using YakUI.App.Resources;
using YakUI.App.Services;
using YakUI.Core.Catalog;
using YakUI.Core.Compat;
using YakUI.Core.Targets;

namespace YakUI.App.ViewModels;

public sealed record SortOption(CatalogSort Sort, string Title, bool DefaultDescending);

/// <summary>Данные для экрана «Каталог»: снимок кэша, установленные версии, приоритет краулера.</summary>
public interface ICatalogSource
{
    CatalogSnapshot LoadSnapshot();

    IReadOnlyDictionary<string, string> InstalledFor(TargetChoice? choice);

    void Prioritize(IEnumerable<string> names);
}

/// <summary>
/// Экран «Каталог» (8, FR-21…FR-27): поиск, фильтры совместимости по выбранной цели, Mac-only, prerelease,
/// «установленные», «есть обновление», сортировки. Запрос выполняется в фоне; поиск — с задержкой 250 мс.
/// Пакеты «уточняется» из результата поднимаются в начало очереди краулера.
/// </summary>
public sealed partial class CatalogViewModel : ScreenViewModel
{
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(250);

    private readonly ICatalogSource _source;
    private readonly TargetSelectorViewModel _target;
    private readonly IUiDispatcher _ui;
    private readonly IIconProvider _icons;
    private CatalogSnapshot _snapshot = CatalogSnapshot.Empty;
    private CancellationTokenSource? _pending;
    private int _version;

    public CatalogViewModel(ICatalogSource source, IPackageCardSource cardSource, TargetSelectorViewModel target, IUiDispatcher ui, IIconProvider? icons = null)
        : base(ScreenKind.Catalog, Strings.Nav_Catalog)
    {
        _source = source;
        _icons = icons ?? new PlaceholderIconProvider();
        _target = target;
        _ui = ui;
        Card = new PackageCardViewModel(cardSource, ui);

        SortOptions =
        [
            new(CatalogSort.Downloads, Strings.Cat_SortDownloads, true),
            new(CatalogSort.DownloadsLastMonth, Strings.Cat_SortMonth, true),
            new(CatalogSort.Name, Strings.Cat_SortName, false),
            new(CatalogSort.Updated, Strings.Cat_SortUpdated, true),
            new(CatalogSort.Created, Strings.Cat_SortCreated, true),
        ];
        _selectedSort = SortOptions[0];

        _target.PropertyChanged += OnTargetChanged;
    }

    public IReadOnlyList<SortOption> SortOptions { get; }

    /// <summary>Текущий снимок каталога (для дат версий на других экранах).</summary>
    public CatalogSnapshot Snapshot => _snapshot;

    /// <summary>Карточка выбранного пакета (FR-30).</summary>
    public PackageCardViewModel Card { get; }

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private SortOption _selectedSort;

    [ObservableProperty]
    private bool _descending = true;

    [ObservableProperty]
    private bool _showNative = true;

    [ObservableProperty]
    private bool _showBackward = true;

    [ObservableProperty]
    private bool _showUnrestricted = true;

    [ObservableProperty]
    private bool _showIncompatible;

    [ObservableProperty]
    private bool _showWindows = true;

    [ObservableProperty]
    private bool _showMac;

    [ObservableProperty]
    private bool _hidePrerelease = true;

    [ObservableProperty]
    private bool _onlyInstalled;

    [ObservableProperty]
    private bool _onlyWithUpdate;

    [ObservableProperty]
    private ObservableCollection<CatalogItemViewModel> _items = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private CatalogItemViewModel? _selectedItem;

    [ObservableProperty]
    private string _countText = "";

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private bool _isCacheEmpty = true;

    public bool HasSelection => SelectedItem is not null;

    /// <summary>Фильтр совместимости выключен («Любая версия», FR-81d): переключатели статусов неактивны.</summary>
    public bool IsCompatibilityFilterOn => _target.CurrentRelease is not null;

    /// <summary>Перечитать снимок кэша (при запуске, после построения кэша краулером) и обновить список.</summary>
    public Task ReloadAsync() => Task.Run(() =>
    {
        var snapshot = _source.LoadSnapshot();
        _icons.PrefetchAll(snapshot);
        _ui.Post(() =>
        {
            _snapshot = snapshot;
            IsCacheEmpty = snapshot.Packages.Count == 0;
            Refresh(immediate: true);
        });
    });

    private string? _selectAfterRefresh;

    /// <summary>Показать пакет: поиск по имени и выбор строки (FR-58 «показать в каталоге»).</summary>
    public void ShowPackage(string name)
    {
        _selectAfterRefresh = name;
        if (SearchText == name)
            Refresh(immediate: true);
        else
            SearchText = name;
    }

    /// <summary>Текущий фильтр по состоянию переключателей и выбранной цели.</summary>
    public CatalogFilter BuildFilter()
    {
        var statuses = new HashSet<CompatibilityStatus>();
        if (ShowNative)
            statuses.Add(CompatibilityStatus.Native);
        if (ShowBackward)
            statuses.Add(CompatibilityStatus.BackwardCompatible);
        if (ShowUnrestricted)
            statuses.Add(CompatibilityStatus.Unrestricted);
        if (ShowIncompatible)
            statuses.Add(CompatibilityStatus.Incompatible);
        if (statuses.Count > 0)
            statuses.Add(CompatibilityStatus.Unknown); // ни одной галочки — фильтра по совместимости нет (FR-22)

        var platforms = new HashSet<CatalogPlatform>();
        if (ShowWindows)
            platforms.Add(CatalogPlatform.Windows);
        if (ShowMac)
            platforms.Add(CatalogPlatform.Mac);

        return new CatalogFilter
        {
            Text = SearchText,
            Target = _target.CurrentRelease,
            Statuses = statuses,
            Platforms = platforms,
            HidePrerelease = HidePrerelease,
            OnlyInstalled = OnlyInstalled,
            OnlyWithUpdate = OnlyWithUpdate,
            Sort = SelectedSort.Sort,
            Descending = Descending,
        };
    }

    /// <summary>Последний запущенный запрос (для тестов).</summary>
    internal Task LastRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>Запуск запроса; без <paramref name="immediate"/> — с задержкой (набор текста).</summary>
    internal void Refresh(bool immediate)
    {
        _pending?.Cancel();
        var cts = _pending = new CancellationTokenSource();
        var version = ++_version;
        var filter = BuildFilter();
        var snapshot = _snapshot;
        var choice = _target.Current;

        LastRefresh = Task.Run(async () =>
        {
            if (!immediate)
                await Task.Delay(SearchDelay, cts.Token).ConfigureAwait(false);

            var installed = _source.InstalledFor(choice);
            var result = CatalogQuery.Run(snapshot, filter, installed);
            var items = new ObservableCollection<CatalogItemViewModel>(result.Entries.Select(e => new CatalogItemViewModel(e, _icons.For(e.Package))));

            _source.Prioritize(result.Entries.Where(e => e.IsPending).Take(60).Select(e => e.Package.Name));

            _ui.Post(() =>
            {
                if (version != _version)
                    return; // устаревший ответ
                var selected = _selectAfterRefresh ?? SelectedItem?.Name;
                Items = items;
                var match = selected is null ? null : items.FirstOrDefault(i => string.Equals(i.Name, selected, StringComparison.OrdinalIgnoreCase));
                if (match is not null || _selectAfterRefresh is null)
                    SelectedItem = match;
                if (match is not null)
                    _selectAfterRefresh = null;
                IsEmpty = items.Count == 0 && !IsCacheEmpty;
                CountText = result.PendingPackages > 0
                    ? string.Format(CultureInfo.CurrentCulture, Strings.Cat_CountPending, items.Count, result.TotalPackages, result.PendingPackages)
                    : string.Format(CultureInfo.CurrentCulture, Strings.Cat_Count, items.Count, result.TotalPackages);
                OnPropertyChanged(nameof(IsCompatibilityFilterOn));
            });
        }, cts.Token);
    }

    partial void OnSelectedItemChanged(CatalogItemViewModel? value)
    {
        // Тот же пакет после обновления списка — карточку не перезагружаем, если цель не менялась.
        if (value is not null && Card.Item?.Name == value.Name && Equals(_cardTarget, _target.CurrentRelease))
            return;
        _cardTarget = _target.CurrentRelease;
        Card.Show(value, _cardTarget);
    }

    private RhinoRelease? _cardTarget;

    partial void OnSearchTextChanged(string value) => Refresh(immediate: false);

    partial void OnSelectedSortChanged(SortOption value)
    {
        Descending = value.DefaultDescending;
        Refresh(immediate: true);
    }

    partial void OnDescendingChanged(bool value) => Refresh(immediate: true);

    partial void OnShowNativeChanged(bool value) => Refresh(immediate: true);

    partial void OnShowBackwardChanged(bool value) => Refresh(immediate: true);

    partial void OnShowUnrestrictedChanged(bool value) => Refresh(immediate: true);

    partial void OnShowIncompatibleChanged(bool value) => Refresh(immediate: true);

    partial void OnShowWindowsChanged(bool value) => Refresh(immediate: true);

    partial void OnShowMacChanged(bool value) => Refresh(immediate: true);

    partial void OnHidePrereleaseChanged(bool value) => Refresh(immediate: true);

    partial void OnOnlyInstalledChanged(bool value) => Refresh(immediate: true);

    partial void OnOnlyWithUpdateChanged(bool value) => Refresh(immediate: true);

    private void OnTargetChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TargetSelectorViewModel.CurrentRelease) or nameof(TargetSelectorViewModel.SelectedItem))
            Refresh(immediate: true);
    }
}
