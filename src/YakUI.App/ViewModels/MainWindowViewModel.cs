using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YakUI.App.Resources;
using YakUI.App.Services;

namespace YakUI.App.ViewModels;

/// <summary>Главное окно: выпадающий список Rhino, навигация по экранам, строка фоновых операций (FR-80, FR-81, FR-84).</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(TargetSelectorViewModel target, BackgroundOperations operations, CatalogStatusViewModel catalogStatus, CatalogViewModel catalog, InstalledViewModel? installed = null, DownloadsViewModel? downloads = null,
        JournalViewModel? journal = null, SettingsViewModel? settings = null, HostsViewModel? hosts = null)
    {
        Target = target;
        Operations = operations;
        CatalogStatus = catalogStatus;
        Catalog = catalog;
        Hosts = hosts;

        Screens =
        [
            // FR-80a: «Источники» и «Наборы» (этап 5) в 1.0 не показываются.
            catalog,
            (ScreenViewModel?)installed ?? new PlaceholderScreenViewModel(ScreenKind.Installed, Strings.Nav_Installed, PlaceholderScreenViewModel.Step("2.7"), target),
            (ScreenViewModel?)downloads ?? new PlaceholderScreenViewModel(ScreenKind.Downloads, Strings.Nav_Downloads, PlaceholderScreenViewModel.Step("2.8")),
            (ScreenViewModel?)journal ?? new PlaceholderScreenViewModel(ScreenKind.Journal, Strings.Nav_Journal, PlaceholderScreenViewModel.Step("2.11")),
            (ScreenViewModel?)settings ?? new PlaceholderScreenViewModel(ScreenKind.Settings, Strings.Nav_Settings, PlaceholderScreenViewModel.Step("2.11")),
        ];
        _selectedScreen = Screens[0];

        Installed = installed;
        if (installed is not null)
        {
            // FR-58: «показать в каталоге».
            installed.ShowInCatalogRequested += name =>
            {
                SelectedScreen = Catalog;
                Catalog.ShowPackage(name);
            };
        }
    }

    public InstalledViewModel? Installed { get; }

    /// <summary>«Открыть hosts» (14.2); null — в тестах без платформы.</summary>
    public HostsViewModel? Hosts { get; }

    partial void OnSelectedScreenChanged(ScreenViewModel value)
    {
        // Профиль мог измениться вне YakUI — список перечитывается при каждом открытии экрана.
        if (value.Kind == ScreenKind.Installed)
            Installed?.Refresh();
        else if (value is JournalViewModel journal)
            journal.Refresh();
        else if (value is SettingsViewModel settings)
            settings.RebuildYak();
    }

    public TargetSelectorViewModel Target { get; }

    public BackgroundOperations Operations { get; }

    public CatalogStatusViewModel CatalogStatus { get; }

    public CatalogViewModel Catalog { get; }

    public ObservableCollection<ScreenViewModel> Screens { get; }

    [ObservableProperty]
    private ScreenViewModel _selectedScreen;

    /// <summary>Перетаскивание файлов в окно (FR-101): импорт на экране «Установленные».</summary>
    public async Task ImportFilesAsync(IReadOnlyList<string> files)
    {
        if (Installed is null)
            return;
        SelectedScreen = Installed;
        await Installed.ImportAsync(files);
    }

    /// <summary>
    /// Ссылка «Food4Rhino» в строке состояния: выбран пакет в каталоге — поиск этого пакета на Food4Rhino, нет — главная (FR-100).
    /// </summary>
    [RelayCommand]
    private void OpenFood4Rhino() => Catalog.Card.SearchFood4RhinoCommand.Execute(null);

    /// <summary>Открыть «Настройки» (левая панель; кнопки ⚙ больше нет).</summary>
    [RelayCommand]
    private void OpenSettings() => SelectedScreen = Screens.First(s => s.Kind == ScreenKind.Settings);
}
