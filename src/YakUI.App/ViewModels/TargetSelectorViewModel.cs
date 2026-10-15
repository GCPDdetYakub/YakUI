using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using YakUI.App.Resources;
using YakUI.Core.Compat;
using YakUI.Core.Targets;

namespace YakUI.App.ViewModels;

/// <summary>Строка выпадающего списка Rhino: пункт или заголовок группы (14.1).</summary>
public sealed class TargetListItem
{
    private TargetListItem(TargetChoice? choice, string title, string? subtitle, string? tooltip, bool isHeader, bool isSeparator)
    {
        Choice = choice;
        Title = title;
        Subtitle = subtitle;
        Tooltip = tooltip;
        IsHeader = isHeader;
        IsSeparator = isSeparator;
    }

    public TargetChoice? Choice { get; }

    public string Title { get; }

    public string? Subtitle { get; }

    public string? Tooltip { get; }

    public bool IsHeader { get; }

    public bool IsSeparator { get; }

    public bool IsSelectable => Choice is not null;

    public bool IsInstalled => Choice?.Kind == TargetChoiceKind.Installed;

    /// <summary>RC, Beta или WIP (FR-81g): пометка в названии и цвет.</summary>
    public bool IsPrerelease => Choice?.Target?.IsPrerelease == true;

    public static TargetListItem Header(string title) => new(null, title, null, null, true, false);

    public static TargetListItem Separator() => new(null, "", null, null, false, true);

    public static TargetListItem For(TargetChoice c) => c.Kind switch
    {
        TargetChoiceKind.Installed => new(c, c.Target!.DisplayName, c.Target.InstallPath, InstalledTooltip(c.Target), false, false),
        TargetChoiceKind.NotInstalled => new(c, string.Create(CultureInfo.InvariantCulture, $"Rhino {c.Major}"), Strings.Target_NotInstalledSuffix, null, false, false),
        _ => new(c, Strings.Target_Any, null, null, false, false),
    };

    /// <summary>FR-81f: профиль пакетов, Grasshopper Libraries, встроенный yak.</summary>
    private static string InstalledTooltip(RhinoTarget t) => new StringBuilder()
        .Append(t.IsPrerelease ? Strings.Target_Prerelease + Environment.NewLine : "")
        .AppendLine(CultureInfo.CurrentCulture, $"{Strings.Target_TooltipProfile}: {t.UserPackagesDirectory}")
        .AppendLine(CultureInfo.CurrentCulture, $"{Strings.Target_TooltipGhLibraries}: {t.GrasshopperLibrariesDirectory}")
        .Append(CultureInfo.CurrentCulture, $"{Strings.Target_TooltipYak}: {t.BuiltInYakPath ?? Strings.Target_TooltipNoYak}")
        .ToString();
}

/// <summary>Пункт списка «Сервис-релиз»; <see cref="Minor"/> = null — «последний» (FR-81c).</summary>
public sealed record ServiceReleaseOption(int? Minor, string Title);

/// <summary>Сохранение выбора между запусками (FR-81f).</summary>
public interface ITargetSelectionStore
{
    (string? Key, int? ServiceRelease) Load();

    void Save(string key, int? serviceRelease);
}

/// <summary>
/// Выпадающий список Rhino в заголовке окна (FR-81, 14.1). От выбора зависят фильтр совместимости
/// (<see cref="CurrentRelease"/>) и доступные действия (<see cref="Capabilities"/>).
/// </summary>
public sealed partial class TargetSelectorViewModel : ObservableObject
{
    private readonly ITargetSelectionStore _store;
    private bool _restoring;

    public TargetSelectorViewModel(ITargetSelectionStore store) => _store = store;

    public ObservableCollection<TargetListItem> Items { get; } = [];

    public ObservableCollection<ServiceReleaseOption> ServiceReleases { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(IsServiceReleaseVisible), nameof(Capabilities), nameof(CurrentRelease), nameof(CapabilitiesText), nameof(FilterText))]
    private TargetListItem? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentRelease), nameof(FilterText))]
    private ServiceReleaseOption? _selectedServiceRelease;

    public TargetChoice? Current => SelectedItem?.Choice;

    /// <summary>Второй список виден только для неустановленной версии (FR-81c).</summary>
    public bool IsServiceReleaseVisible => Current?.Kind == TargetChoiceKind.NotInstalled;

    public TargetCapabilities Capabilities => Current?.Capabilities ?? TargetCapabilities.For(TargetChoiceKind.AnyVersion);

    /// <summary>Версия для фильтра совместимости; null — без фильтра.</summary>
    public RhinoRelease? CurrentRelease => Current?.ReleaseFor(SelectedServiceRelease?.Minor);

    public string CapabilitiesText => Current?.Kind switch
    {
        TargetChoiceKind.Installed => Strings.Caps_Installed,
        TargetChoiceKind.NotInstalled => Strings.Caps_NotInstalled,
        _ => Strings.Caps_Any,
    };

    public string? FilterText => CurrentRelease is { } r
        ? string.Format(CultureInfo.CurrentCulture, Strings.Caps_FilterRelease, r)
        : null;

    /// <summary>Пересобирает список (при запуске и по мере пополнения каталога), сохраняя выбор.</summary>
    public void Rebuild(IReadOnlyList<TargetChoice> choices)
    {
        _restoring = true;
        try
        {
            var (savedKey, savedSr) = Current is { } current
                ? (current.Key, SelectedServiceRelease?.Minor)
                : _store.Load();

            Items.Clear();
            var installed = choices.Where(c => c.Kind == TargetChoiceKind.Installed).ToList();
            var notInstalled = choices.Where(c => c.Kind == TargetChoiceKind.NotInstalled).ToList();

            Items.Add(TargetListItem.Header(Strings.Target_GroupInstalled));
            foreach (var c in installed)
                Items.Add(TargetListItem.For(c));
            if (notInstalled.Count > 0)
            {
                Items.Add(TargetListItem.Header(Strings.Target_GroupNotInstalled));
                foreach (var c in notInstalled)
                    Items.Add(TargetListItem.For(c));
            }

            Items.Add(TargetListItem.Separator());
            Items.Add(TargetListItem.For(TargetChoice.Any));

            var restored = TargetChoiceBuilder.Restore(choices, savedKey);
            SelectedItem = Items.First(i => i.Choice?.Key == restored.Key);
            RebuildServiceReleases(restored.Key == savedKey ? savedSr : null);
        }
        finally
        {
            _restoring = false;
        }

        Persist();
    }

    partial void OnSelectedItemChanged(TargetListItem? oldValue, TargetListItem? newValue)
    {
        // Заголовки и разделитель не выбираются: возвращаем прежний пункт.
        if (newValue is { IsSelectable: false })
        {
            SelectedItem = oldValue;
            return;
        }

        if (_restoring)
            return;
        RebuildServiceReleases(null);
        Persist();
    }

    partial void OnSelectedServiceReleaseChanged(ServiceReleaseOption? value)
    {
        if (!_restoring)
            Persist();
    }

    /// <summary>Безопасный выбор из UI: клик по заголовку игнорируется.</summary>
    public void Select(TargetListItem item)
    {
        if (item.IsSelectable)
            SelectedItem = item;
    }

    private void RebuildServiceReleases(int? preferred)
    {
        var wasRestoring = _restoring;
        _restoring = true;
        try
        {
            ServiceReleases.Clear();
            if (Current is not { Kind: TargetChoiceKind.NotInstalled } c)
            {
                SelectedServiceRelease = null;
                return;
            }

            var latest = c.ServiceReleases.Count > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{c.Major}.{c.ServiceReleases[^1]} ({Strings.Target_ServiceReleaseLatest})")
                : Strings.Target_ServiceReleaseLatest;
            ServiceReleases.Add(new ServiceReleaseOption(null, latest));
            foreach (var minor in c.ServiceReleases.Reverse())
                ServiceReleases.Add(new ServiceReleaseOption(minor, string.Create(CultureInfo.InvariantCulture, $"{c.Major}.{minor}")));

            SelectedServiceRelease = ServiceReleases.FirstOrDefault(o => o.Minor == preferred && preferred is not null) ?? ServiceReleases[0];
        }
        finally
        {
            _restoring = wasRestoring;
        }
    }

    private void Persist()
    {
        if (Current is { } c)
            _store.Save(c.Key, c.Kind == TargetChoiceKind.NotInstalled ? SelectedServiceRelease?.Minor : null);
    }
}
