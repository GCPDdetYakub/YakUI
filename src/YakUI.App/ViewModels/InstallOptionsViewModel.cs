using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YakUI.App.Resources;
using YakUI.Core.Compat;
using YakUI.Core.Install;
using YakUI.Core.Settings;
using YakUI.Core.Targets;

namespace YakUI.App.ViewModels;

/// <summary>
/// Выбор в окне установки: режим и папка (для своей папки и «только распаковать»), среда для ссылки .ghlink
/// (FR-43: пакеты с net48 и net7.0/net8.0), копировать ли найденные рядом зависимости одиночного файла (13a).
/// </summary>
public sealed record InstallChoice(InstallMode Mode, string? Root, DotNetRuntimeKind? Runtime = null, bool IncludeDependencies = true);

public sealed record RuntimeOption(DotNetRuntimeKind Kind, string Title);

/// <summary>Что известно окну установки о пакете.</summary>
public sealed record InstallOptionsContext(
    RhinoTarget Target,
    string Package,
    string Version,
    DistributionTag? Tag,
    bool StandardAllowed,
    bool InStandardProfile,
    bool InCustomPath,
    IReadOnlyList<RhinoTarget> AllTargets,
    IReadOnlyList<CustomRoot> Roots,
    string? LastRoot)
{
    /// <summary>Подпапки сред со сборками Grasshopper (net48, net7.0…).</summary>
    public IReadOnlyList<string> RuntimeFolders { get; init; } = [];

    /// <summary>Зависимости одиночного файла (13a).</summary>
    public DependencyReport Dependencies { get; init; } = DependencyReport.None;
}

public sealed record ModeOption(InstallMode Mode, string Title, string Hint);

/// <summary>
/// Окно установки (FR-40, FR-45): стандартный профиль / пользовательская папка / «только распаковать».
/// Последняя папка цели предлагается по умолчанию (FR-40). Предупреждения: ссылка .ghlink действует во всех версиях Rhino
/// с Grasshopper и где дистрибутив несовместим — будет ошибка загрузки (FR-43, 21.6); пакет уже стоит в другом режиме
/// для той же цели — возможна двойная загрузка (FR-47).
/// </summary>
public sealed partial class InstallOptionsViewModel : ObservableObject
{
    private readonly InstallOptionsContext _c;
    private readonly Func<Task<string?>>? _pickFolder;

    public InstallOptionsViewModel(InstallOptionsContext context, Func<Task<string?>>? pickFolder = null)
    {
        _c = context;
        _pickFolder = pickFolder;
        Modes = new ObservableCollection<ModeOption>(new[]
        {
            context.StandardAllowed ? new ModeOption(InstallMode.Standard, Strings.Io_ModeStandard, string.Format(CultureInfo.CurrentCulture, Strings.Io_ModeStandardHint, context.Target.UserPackagesDirectory)) : null,
            new ModeOption(InstallMode.Custom, Strings.Io_ModeCustom, Strings.Io_ModeCustomHint),
            new ModeOption(InstallMode.Unpack, Strings.Io_ModeUnpack, Strings.Io_ModeUnpackHint),
        }.OfType<ModeOption>());
        Roots = new ObservableCollection<CustomRoot>(context.Roots);
        _selectedRoot = Roots.FirstOrDefault(r => string.Equals(r.Path, context.LastRoot, StringComparison.OrdinalIgnoreCase)) ?? Roots.FirstOrDefault();
        _selectedMode = Modes[0];

        // FR-43 (21.6): у пакета сборки и под .NET Framework, и под .NET (Core) — одна ссылка в общей Libraries не подходит
        // всем средам, где запущен Rhino; стандартный профиль Rhino выбирает подпапку сам.
        var kinds = context.RuntimeFolders.Select(GhLinks.ParseFramework).OfType<(DotNetRuntimeKind Kind, Version Version)>().Select(f => f.Kind).ToHashSet();
        IsMultiRuntime = kinds.Count > 1;
        RuntimeOptions =
        [
            new(DotNetRuntimeKind.NetCore, Strings.Io_RuntimeCore),
            new(DotNetRuntimeKind.NetFramework, Strings.Io_RuntimeFx),
        ];
        _selectedRuntime = RuntimeOptions.First(o => o.Kind == DefaultRuntime(context.Target));
        Title = string.Format(CultureInfo.CurrentCulture, Strings.Io_Title, context.Package, context.Version, context.Target.DisplayName);
    }

    public string Title { get; }

    public ObservableCollection<ModeOption> Modes { get; }

    public ObservableCollection<CustomRoot> Roots { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsRoot), nameof(Warnings), nameof(HasWarnings), nameof(ShowRuntime))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private ModeOption _selectedMode;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyPropertyChangedFor(nameof(Warnings), nameof(HasWarnings))]
    private CustomRoot? _selectedRoot;

    public bool NeedsRoot => SelectedMode.Mode != InstallMode.Standard;

    /// <summary>Среда по умолчанию для ссылки — среда цели по 17a.</summary>
    internal static DotNetRuntimeKind DefaultRuntime(RhinoTarget t) => t.Runtime.Runtime ?? DotNetRuntimeKind.NetFramework;

    /// <summary>Сборки и под .NET Framework, и под .NET (Core) (FR-43).</summary>
    public bool IsMultiRuntime { get; }

    /// <summary>Рекомендация стандартной установки для таких пакетов.</summary>
    public bool RecommendStandard => IsMultiRuntime && _c.StandardAllowed;

    public IReadOnlyList<RuntimeOption> RuntimeOptions { get; }

    /// <summary>Выбор среды — для своей папки у пакета с несколькими сборками.</summary>
    public bool ShowRuntime => IsMultiRuntime && SelectedMode.Mode == InstallMode.Custom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Warnings), nameof(HasWarnings))]
    private RuntimeOption _selectedRuntime;

    /// <summary>Найденные рядом зависимости одиночного файла (13a).</summary>
    public string? DependenciesText => _c.Dependencies.Found.Count == 0 ? null
        : string.Format(CultureInfo.CurrentCulture, Strings.Io_DepsFound, string.Join(", ", _c.Dependencies.Found.Select(Path.GetFileName)));

    public bool HasDependencies => DependenciesText is not null;

    [ObservableProperty]
    private bool _includeDependencies = true;

    /// <summary>Итог: null — отмена.</summary>
    public InstallChoice? Result { get; private set; }

    public event Action? CloseRequested;

    public IReadOnlyList<string> Warnings => BuildWarnings();

    public bool HasWarnings => Warnings.Count > 0;

    internal IReadOnlyList<string> BuildWarnings()
    {
        var list = new List<string>();

        // 13a: одиночному файлу не хватает сборок — он не загрузится.
        if (_c.Dependencies.Missing.Count > 0)
            list.Add(string.Format(CultureInfo.CurrentCulture, Strings.Io_DepsMissing, string.Join(", ", _c.Dependencies.Missing)));
        if (RecommendStandard && SelectedMode.Mode != InstallMode.Standard)
            list.Add(Strings.Io_MultiRuntimeRecommend);
        if (ShowRuntime)
            list.Add(SelectedRuntime.Kind == DotNetRuntimeKind.NetFramework ? Strings.Io_RuntimeFxWarning : Strings.Io_RuntimeCoreWarning);

        if (SelectedMode.Mode == InstallMode.Custom)
        {
            // FR-43, 21.6: общая папка Grasshopper\Libraries.
            var others = _c.AllTargets.Where(t => t.InstallPath != _c.Target.InstallPath).ToList();
            if (others.Count > 0)
            {
                list.Add(string.Format(CultureInfo.CurrentCulture, Strings.Io_GhLinkShared,
                    string.Join(", ", _c.AllTargets.Select(t => t.DisplayName))));
                if (_c.Tag is { } tag)
                {
                    foreach (var t in others)
                    {
                        var c = CompatibilityRules.Evaluate(tag, t.Release);
                        if (c.PlatformMismatch || c.Status == CompatibilityStatus.Incompatible)
                            list.Add(string.Format(CultureInfo.CurrentCulture, Strings.Io_GhLinkIncompatible, tag.Original, t.DisplayName,
                                tag.RhinoMajor?.ToString(CultureInfo.InvariantCulture) ?? "?"));
                    }
                }
            }

            if (_c.InStandardProfile)
                list.Add(string.Format(CultureInfo.CurrentCulture, Strings.Io_Duplicate, _c.Package, Strings.Io_InStandard));
        }
        else if (SelectedMode.Mode == InstallMode.Standard && _c.InCustomPath)
        {
            list.Add(string.Format(CultureInfo.CurrentCulture, Strings.Io_Duplicate, _c.Package, Strings.Io_InCustom));
        }

        return list;
    }

    private bool CanConfirm() => !NeedsRoot || SelectedRoot is not null;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        Result = new InstallChoice(SelectedMode.Mode, NeedsRoot ? SelectedRoot!.Path : null,
            ShowRuntime ? SelectedRuntime.Kind : null, IncludeDependencies);
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke();
    }

    /// <summary>«Другая папка…» — без добавления в список настроек.</summary>
    [RelayCommand]
    private async Task PickFolder()
    {
        if (_pickFolder is null || await _pickFolder() is not { } path)
            return;
        var existing = Roots.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new CustomRoot(Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path, path);
            Roots.Add(existing);
        }

        SelectedRoot = existing;
    }
}
