using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YakUI.App.Resources;
using YakUI.App.Services;
using YakUI.Core.Net;
using YakUI.Core.Settings;
using YakUI.Core.Targets;
using YakUI.Core.Tools;

namespace YakUI.App.ViewModels;

/// <summary>Что делает экран «Настройки» (в приложении — AppHost; в тестах — фейк).</summary>
public interface ISettingsBackend
{
    AppSettings Settings { get; }

    void Save();

    IReadOnlyList<RhinoTarget> Targets { get; }

    IReadOnlyList<YakTool> Tools { get; }

    YakTool? SelectFor(int rhinoMajor);

    /// <summary>Перепроверить yak-инструменты (FR-13) фоновой операцией.</summary>
    Task RefreshYakAsync();

    /// <summary>Применить тему сразу, без перезапуска (FR-83).</summary>
    void ApplyTheme(AppTheme theme);

    string DefaultDownloadsDirectory { get; }

    /// <summary>Проверить и применить папку загрузок (FR-35); текст ошибки или null.</summary>
    string? ApplyDownloadsDirectory(string path);

    Task<string?> PickFolderAsync(string title);

    Task<string?> PickYakAsync(string title);

    bool OpenFolder(string path);

    /// <summary>Применить прокси ко всем запросам YakUI и yak без перезапуска (FR-95, FR-95a).</summary>
    void ApplyProxy(ProxySettings proxy);

    /// <summary>Поиск новых сборок yak (FR-12, 6.2).</summary>
    Task<YakUI.Core.Tools.YakCheckResult> CheckYakUpdatesAsync();

    /// <summary>Скачать, проверить и подключить yak (FR-10, FR-14, FR-17, FR-19); итог одной строкой.</summary>
    Task<string> DownloadYakAsync(string version);

    /// <summary>Последний результат проверки (при запуске или по кнопке).</summary>
    YakUI.Core.Tools.YakCheckResult? LastYakCheck { get; }

    /// <summary>Открыть мастер первого запуска (FR-18).</summary>
    void OpenFirstRunWizard();

    /// <summary>Можно ли писать в папку (FR-41); текст ошибки или null.</summary>
    string? CheckWritable(string path);

    /// <summary>«Проверить соединение» через проверяемый режим (FR-95b).</summary>
    Task<ConnectionReport> CheckConnectionAsync(ProxySettings proxy);
}

public sealed record ProxyModeOption(ProxyMode Mode, string Title);

public sealed record ProxyKindOption(ProxyKind Kind, string Title);

public sealed record ThemeOption(AppTheme Theme, string Title);

public sealed record LanguageOption(AppLanguage Language, string Title);

/// <summary>Пользовательская папка в списке (FR-41): имя правится на месте и сразу сохраняется.</summary>
public sealed partial class CustomRootRowViewModel : ObservableObject
{
    private readonly Action _changed;

    public CustomRootRowViewModel(CustomRoot root, Action changed)
    {
        _name = root.Name;
        Path = root.Path;
        _changed = changed;
    }

    public string Path { get; }

    [ObservableProperty]
    private string _name;

    partial void OnNameChanged(string value) => _changed();

    public CustomRoot ToRoot() => new(string.IsNullOrWhiteSpace(Name) ? System.IO.Path.GetFileName(Path.TrimEnd('\\')) : Name.Trim(), Path);
}

/// <summary>Строка yak-инструмента в библиотеке (FR-13).</summary>
public sealed class YakToolRowViewModel
{
    public YakToolRowViewModel(YakTool tool, bool isSelected, bool isPinned)
    {
        Tool = tool;
        IsSelected = isSelected;
        IsPinned = isPinned;
        VersionText = tool.Version?.ToString() ?? "?";
        OriginText = tool.Origin switch
        {
            YakToolOrigin.BuiltIn => Strings.Set_OriginBuiltIn,
            YakToolOrigin.Downloaded => Strings.Set_OriginDownloaded,
            _ => Strings.Set_OriginManual,
        };
        StatusText = tool.Problem is { } p
            ? string.Format(CultureInfo.CurrentCulture, Strings.Set_YakUnusable, p)
            : isPinned ? Strings.Set_YakPinned
            : isSelected ? Strings.Set_YakSelected
            : Strings.Set_YakUsable;
        BindingText = tool.BoundRhinoMajor is { } m
            ? string.Create(CultureInfo.InvariantCulture, $"Rhino {m}")
            : Strings.Set_YakNoBinding;
    }

    public YakTool Tool { get; }

    public string Path => Tool.Path;

    public string VersionText { get; }

    public string OriginText { get; }

    public string BindingText { get; }

    public string StatusText { get; }

    public bool IsSelected { get; }

    public bool IsPinned { get; }

    public bool IsUsable => Tool.IsUsable;

    public bool HasProblem => Tool.Problem is not null;

    public bool IsManual => Tool.Origin == YakToolOrigin.Manual;

    public bool NeedsConfirmation => Tool.Trust == YakTrust.NeedsConfirmation && Tool.Sha256 is not null;
}

/// <summary>Группа «Rhino N»: выбранный yak и все привязанные к этой версии (FR-13).</summary>
public sealed class YakGroupViewModel
{
    public YakGroupViewModel(string title, string selectedText, IReadOnlyList<YakToolRowViewModel> tools,
        int? rhinoMajor = null, string? availableVersion = null, string? previousPath = null)
    {
        Title = title;
        SelectedText = selectedText;
        Tools = tools;
        RhinoMajor = rhinoMajor;
        AvailableVersion = availableVersion;
        PreviousPath = previousPath;
    }

    public int? RhinoMajor { get; }

    /// <summary>Более новая сборка той же линейки на files.mcneel.com (FR-12).</summary>
    public string? AvailableVersion { get; }

    public string? AvailableText => AvailableVersion is { } v ? string.Format(CultureInfo.CurrentCulture, Strings.Set_YakAvailable, v) : null;

    public bool HasAvailable => AvailableVersion is not null;

    /// <summary>yak, использовавшийся до последнего скачивания (FR-19: откат одной кнопкой).</summary>
    public string? PreviousPath { get; }

    public bool HasPrevious => PreviousPath is not null;

    public string Title { get; }

    public string SelectedText { get; }

    public IReadOnlyList<YakToolRowViewModel> Tools { get; }
}

/// <summary>
/// Экран «Настройки» ⚙ (FR-80, FR-86): тема (FR-83), папка загрузок (FR-35), библиотека yak по версиям Rhino
/// с закреплением, ручными путями и подтверждением неподписанных (FR-13, FR-17). Скачивание yak — этап 4.
/// </summary>
public sealed partial class SettingsViewModel : ScreenViewModel
{
    private readonly ISettingsBackend _backend;
    private readonly IDialogService _dialogs;
    private bool _loading;

    public SettingsViewModel(ISettingsBackend backend, IDialogService dialogs, HostsViewModel? hosts = null)
        : base(ScreenKind.Settings, Strings.Nav_Settings)
    {
        Hosts = hosts;
        _backend = backend;
        _dialogs = dialogs;
        Themes =
        [
            new(AppTheme.System, Strings.Set_ThemeSystem),
            new(AppTheme.Light, Strings.Set_ThemeLight),
            new(AppTheme.Dark, Strings.Set_ThemeDark),
        ];
        _loading = true;
        _selectedTheme = Themes.First(t => t.Theme == backend.Settings.Theme);
        Languages =
        [
            new(AppLanguage.System, Strings.Set_LanguageSystem),
            new(AppLanguage.Russian, "Русский"),
            new(AppLanguage.English, "English"),
        ];
        _selectedLanguage = Languages.First(l => l.Language == backend.Settings.Language);
        _loading = false;
        UpdateDownloads();
        RebuildYak();
        Roots = new ObservableCollection<CustomRootRowViewModel>((backend.Settings.CustomRoots ?? []).Select(r => new CustomRootRowViewModel(r, SaveRoots)));

        ProxyModes =
        [
            new(ProxyMode.System, Strings.Net_ModeSystem),
            new(ProxyMode.None, Strings.Net_ModeNone),
            new(ProxyMode.Manual, Strings.Net_ModeManual),
        ];
        ProxyKinds = [new(ProxyKind.Http, "HTTP"), new(ProxyKind.Socks5, "SOCKS5")];
        var proxy = backend.Settings.Proxy ?? new ProxySettings();
        _selectedProxyMode = ProxyModes.First(m => m.Mode == proxy.Mode);
        _selectedProxyKind = ProxyKinds.First(k => k.Kind == proxy.Kind);
        _proxyAddress = string.IsNullOrWhiteSpace(proxy.Host) ? "" : string.Create(CultureInfo.InvariantCulture, $"{proxy.Host}:{proxy.Port}");
    }

    // ---- Пользовательские папки (FR-41) ----

    public ObservableCollection<CustomRootRowViewModel> Roots { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveRootCommand))]
    private CustomRootRowViewModel? _selectedRootRow;

    [ObservableProperty]
    private string? _rootsMessage;

    private void SaveRoots()
    {
        _backend.Settings.CustomRoots = Roots.Select(r => r.ToRoot()).ToList();
        _backend.Save();
    }

    [RelayCommand]
    private async Task AddRoot()
    {
        if (await _backend.PickFolderAsync(Strings.Set_PickRoot) is not { } path)
            return;
        if (_backend.CheckWritable(path) is { } error)
        {
            RootsMessage = string.Format(CultureInfo.CurrentCulture, Strings.Set_RootNotWritable, error);
            return;
        }

        if (Roots.All(r => !string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            var name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
            Roots.Add(new CustomRootRowViewModel(new CustomRoot(name.Length == 0 ? path : name, path), SaveRoots));
            SaveRoots();
        }

        RootsMessage = string.Format(CultureInfo.CurrentCulture, Strings.Set_RootAdded, path);
    }

    private bool CanRemoveRoot() => SelectedRootRow is not null;

    /// <summary>Убрать из списка; файлы и установки не трогаются.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveRoot))]
    private void RemoveRoot()
    {
        if (SelectedRootRow is { } row)
        {
            Roots.Remove(row);
            SaveRoots();
        }
    }

    // ---- Сеть: прокси (FR-95…FR-95b) ----

    public IReadOnlyList<ProxyModeOption> ProxyModes { get; }

    public IReadOnlyList<ProxyKindOption> ProxyKinds { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManualProxy))]
    private ProxyModeOption _selectedProxyMode;

    [ObservableProperty]
    private ProxyKindOption _selectedProxyKind;

    /// <summary>«host:port» или «socks5://host:port».</summary>
    [ObservableProperty]
    private string _proxyAddress;

    [ObservableProperty]
    private string? _proxyMessage;

    [ObservableProperty]
    private string? _connectionText;

    [ObservableProperty]
    private bool _connectionOk;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckConnectionCommand))]
    private bool _isCheckingConnection;

    public bool IsManualProxy => SelectedProxyMode.Mode == ProxyMode.Manual;

    /// <summary>Настройки из полей ввода; null и текст ошибки — ввод неверен.</summary>
    internal (ProxySettings? Settings, string? Error) ReadProxy()
    {
        if (SelectedProxyMode.Mode != ProxyMode.Manual)
            return (new ProxySettings { Mode = SelectedProxyMode.Mode, Kind = SelectedProxyKind.Kind }, null);
        var parsed = ProxySettings.ParseAddress(ProxyAddress, SelectedProxyKind.Kind);
        if (parsed is null)
            return (null, Strings.Net_BadAddress);
        return parsed.Validate() is { } error ? (null, error) : (parsed, null);
    }

    [RelayCommand]
    private void ApplyProxy()
    {
        var (proxy, error) = ReadProxy();
        if (proxy is null)
        {
            ProxyMessage = error;
            return;
        }

        _backend.Settings.Proxy = proxy;
        _backend.Save();
        _backend.ApplyProxy(proxy);
        if (proxy.Mode == ProxyMode.Manual)
        {
            SelectedProxyKind = ProxyKinds.First(k => k.Kind == proxy.Kind);
            ProxyAddress = string.Create(CultureInfo.InvariantCulture, $"{proxy.Host}:{proxy.Port}");
        }

        ProxyMessage = Strings.Net_Applied;
    }

    private bool CanCheckConnection() => !IsCheckingConnection;

    [RelayCommand(CanExecute = nameof(CanCheckConnection))]
    private async Task CheckConnection()
    {
        var (proxy, error) = ReadProxy();
        if (proxy is null)
        {
            ConnectionOk = false;
            ConnectionText = error;
            return;
        }

        IsCheckingConnection = true;
        ConnectionText = Strings.Net_Checking;
        try
        {
            var r = await _backend.CheckConnectionAsync(proxy);
            ConnectionOk = r.Ok;
            ConnectionText = string.Join(Environment.NewLine, r.Steps.Select(s =>
                string.Format(CultureInfo.CurrentCulture, s.Ok ? Strings.Net_StepOk : Strings.Net_StepFailed, s.Target, s.Message)));
        }
        catch (Exception e)
        {
            ConnectionOk = false;
            ConnectionText = e.Message;
        }
        finally
        {
            IsCheckingConnection = false;
        }
    }

    public IReadOnlyList<ThemeOption> Themes { get; }

    /// <summary>Разделы «Редактор файла hosts» и «Файл hosts» (FR-89, FR-89h); null — в тестах без платформы.</summary>
    public HostsViewModel? Hosts { get; }

    public IReadOnlyList<LanguageOption> Languages { get; }

    [ObservableProperty]
    private LanguageOption _selectedLanguage;

    /// <summary>Язык отличается от того, с которым запущено окно, — нужен перезапуск.</summary>
    [ObservableProperty]
    private bool _languageNeedsRestart;

    [ObservableProperty]
    private ThemeOption _selectedTheme;

    [ObservableProperty]
    private string _downloadsDirectory = "";

    [ObservableProperty]
    private bool _isDefaultDownloads;

    [ObservableProperty]
    private string? _downloadsMessage;

    [ObservableProperty]
    private ObservableCollection<YakGroupViewModel> _yakGroups = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PinCommand), nameof(UnpinCommand), nameof(RemoveManualCommand), nameof(ConfirmUnsignedCommand))]
    private YakToolRowViewModel? _selectedTool;

    [ObservableProperty]
    private string? _yakMessage;

    [ObservableProperty]
    private bool _isRefreshingYak;

    partial void OnSelectedThemeChanged(ThemeOption value)
    {
        if (_loading)
            return;
        _backend.Settings.Theme = value.Theme;
        _backend.Save();
        _backend.ApplyTheme(value.Theme);
    }

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (_loading)
            return;
        _backend.Settings.Language = value.Language;
        _backend.Save();
        LanguageNeedsRestart = YakUI.Core.Localization.UiLanguage.Resolve(value.Language).Name != System.Globalization.CultureInfo.CurrentUICulture.Name;
    }

    // ---- Папка загрузок (FR-35) ----

    private void UpdateDownloads()
    {
        var custom = _backend.Settings.DownloadsDirectory;
        IsDefaultDownloads = string.IsNullOrWhiteSpace(custom);
        DownloadsDirectory = IsDefaultDownloads ? _backend.DefaultDownloadsDirectory : custom!;
    }

    [RelayCommand]
    private async Task ChangeDownloads()
    {
        if (await _backend.PickFolderAsync(Strings.Set_PickDownloads) is { } path)
            SetDownloads(path);
    }

    [RelayCommand]
    private void ResetDownloads() => SetDownloads(null);

    [RelayCommand]
    private void OpenDownloads() => _backend.OpenFolder(DownloadsDirectory);

    internal void SetDownloads(string? path)
    {
        var target = path ?? _backend.DefaultDownloadsDirectory;
        if (_backend.ApplyDownloadsDirectory(target) is { } error)
        {
            DownloadsMessage = string.Format(CultureInfo.CurrentCulture, Strings.Set_DownloadsFailed, error);
            return;
        }

        _backend.Settings.DownloadsDirectory = path;
        _backend.Save();
        UpdateDownloads();
        DownloadsMessage = Strings.Set_DownloadsChanged;
    }

    // ---- Библиотека yak (FR-13) ----

    internal void RebuildYak()
    {
        var pins = _backend.Settings.PinnedYak ?? [];
        var majors = _backend.Targets.Select(t => t.Major)
            .Concat(_backend.Tools.Where(t => t.BoundRhinoMajor is not null).Select(t => t.BoundRhinoMajor!.Value))
            .Distinct()
            .OrderDescending()
            .ToList();

        var groups = new List<YakGroupViewModel>();
        foreach (var major in majors)
        {
            var selected = _backend.SelectFor(major);
            var pinned = pins.TryGetValue(major, out var p) ? p : null;
            var rows = _backend.Tools.Where(t => t.BoundRhinoMajor == major)
                .OrderByDescending(t => t.Version)
                .Select(t => new YakToolRowViewModel(t, ReferenceEquals(t, selected), string.Equals(t.Path, pinned, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var selectedText = selected is null
                ? Strings.Set_YakNone
                : string.Format(CultureInfo.CurrentCulture, pinned is null ? Strings.Set_YakUsesNewest : Strings.Set_YakUsesPinned, selected.Version, selected.Path);
            if (pinned is not null && selected is not null && !string.Equals(selected.Path, pinned, StringComparison.OrdinalIgnoreCase))
                selectedText += " " + string.Format(CultureInfo.CurrentCulture, Strings.Set_YakPinUnusable, pinned);
            var available = _backend.LastYakCheck?.Lines.FirstOrDefault(l => l.RhinoMajor == major)?.Newer?.Version.ToString();
            var previous = (_backend.Settings.PreviousYak ?? []).TryGetValue(major, out var prev)
                           && !string.Equals(prev, selected?.Path, StringComparison.OrdinalIgnoreCase)
                           && _backend.Tools.Any(t => t.IsUsable && t.BoundRhinoMajor == major && string.Equals(t.Path, prev, StringComparison.OrdinalIgnoreCase))
                ? prev
                : null;
            groups.Add(new YakGroupViewModel(string.Create(CultureInfo.InvariantCulture, $"Rhino {major}"), selectedText, rows, major, available, previous));
        }

        var unbound = _backend.Tools.Where(t => t.BoundRhinoMajor is null)
            .Select(t => new YakToolRowViewModel(t, false, false)).ToList();
        if (unbound.Count > 0)
            groups.Add(new YakGroupViewModel(Strings.Set_YakUnbound, Strings.Set_YakUnboundHint, unbound));

        var selectedPath = SelectedTool?.Path;
        YakGroups = new ObservableCollection<YakGroupViewModel>(groups);
        SelectedTool = selectedPath is null ? null : groups.SelectMany(g => g.Tools).FirstOrDefault(r => r.Path == selectedPath);
    }

    private bool CanPin() => SelectedTool is { IsUsable: true, IsPinned: false, Tool.BoundRhinoMajor: not null };

    private bool CanUnpin() => SelectedTool is { IsPinned: true };

    private bool CanRemoveManual() => SelectedTool is { IsManual: true };

    private bool CanConfirmUnsigned() => SelectedTool is { NeedsConfirmation: true };

    /// <summary>Закрепить выбранный yak для его версии Rhino (FR-13).</summary>
    [RelayCommand(CanExecute = nameof(CanPin))]
    private void Pin()
    {
        if (SelectedTool?.Tool is not { BoundRhinoMajor: { } major } tool)
            return;
        (_backend.Settings.PinnedYak ??= [])[major] = tool.Path;
        _backend.Save();
        YakMessage = string.Format(CultureInfo.CurrentCulture, Strings.Set_YakPinDone, tool.Version, major);
        RebuildYak();
    }

    [RelayCommand(CanExecute = nameof(CanUnpin))]
    private void Unpin()
    {
        if (SelectedTool?.Tool is not { BoundRhinoMajor: { } major })
            return;
        _backend.Settings.PinnedYak?.Remove(major);
        _backend.Save();
        YakMessage = string.Format(CultureInfo.CurrentCulture, Strings.Set_YakUnpinDone, major);
        RebuildYak();
    }

    /// <summary>Указать yak.exe вручную (FR-13); подлинность проверяется при перепроверке (FR-17).</summary>
    [RelayCommand]
    private async Task AddManual()
    {
        if (await _backend.PickYakAsync(Strings.Set_PickYak) is not { } path)
            return;
        var list = _backend.Settings.ManualYakPaths ??= [];
        if (!list.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(path);
            _backend.Save();
        }

        await RefreshYak();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveManual))]
    private async Task RemoveManual()
    {
        if (SelectedTool?.Tool is not { } tool)
            return;
        _backend.Settings.ManualYakPaths?.RemoveAll(p => string.Equals(p, tool.Path, StringComparison.OrdinalIgnoreCase));
        foreach (var key in (_backend.Settings.PinnedYak ?? []).Where(kv => string.Equals(kv.Value, tool.Path, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
            _backend.Settings.PinnedYak!.Remove(key);
        _backend.Save();
        await RefreshYak();
    }

    /// <summary>Ручной неподписанный yak — только после подтверждения пользователя (FR-17), по SHA-256.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirmUnsigned))]
    private async Task ConfirmUnsigned()
    {
        if (SelectedTool?.Tool is not { Sha256: { } sha } tool)
            return;
        var text = string.Format(CultureInfo.CurrentCulture, Strings.Set_YakConfirmUnsigned, tool.Path, sha);
        if (!await _dialogs.ConfirmAsync(Strings.Set_YakConfirmTitle, text, Strings.Set_YakConfirmButton))
            return;
        var list = _backend.Settings.ConfirmedUnsignedYakSha256 ??= [];
        if (!list.Contains(sha, StringComparer.OrdinalIgnoreCase))
            list.Add(sha);
        _backend.Save();
        await RefreshYak();
    }

    [ObservableProperty]
    private string _yakVersionToDownload = "";

    [ObservableProperty]
    private string? _yakCheckText;

    /// <summary>«Проверить обновления yak» (FR-12).</summary>
    [RelayCommand]
    private async Task CheckYakUpdates()
    {
        IsRefreshingYak = true;
        try
        {
            var r = await _backend.CheckYakUpdatesAsync();
            YakCheckText = FormatCheck(r);
        }
        catch (Exception e)
        {
            YakCheckText = e.Message;
        }
        finally
        {
            IsRefreshingYak = false;
            RebuildYak();
        }
    }

    internal static string FormatCheck(YakUI.Core.Tools.YakCheckResult r)
    {
        var lines = r.Lines.Select(l => l.Newer is { } n
            ? string.Format(CultureInfo.CurrentCulture, Strings.Set_YakLineNewer, l.RhinoMajor, l.Current?.Version?.ToString() ?? "—", n.Version)
            : string.Format(CultureInfo.CurrentCulture, Strings.Set_YakLineCurrent, l.RhinoMajor, l.Current?.Version?.ToString() ?? "—")).ToList();
        lines.Insert(0, string.Format(CultureInfo.CurrentCulture, Strings.Set_YakOnServer,
            r.Available.Count == 0 ? "—" : string.Join(", ", r.Available.Select(a => a.Version.ToString()))));
        if (r.Problem is not null)
            lines.Add(r.Problem);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>«Скачать» более новую сборку для версии Rhino (FR-10, FR-19: рядом, без замены на лету).</summary>
    [RelayCommand]
    private Task DownloadAvailableYak(YakGroupViewModel? group) =>
        group?.AvailableVersion is { } v ? DownloadYakVersion(v) : Task.CompletedTask;

    /// <summary>Скачать указанную версию (6.2, шаг 3 — вручную).</summary>
    [RelayCommand]
    private Task DownloadYakManual() => DownloadYakVersion(YakVersionToDownload.Trim());

    private async Task DownloadYakVersion(string version)
    {
        if (!YakUI.Core.Versioning.PackageVersion.TryParse(version, out _))
        {
            YakMessage = Strings.Set_YakBadVersion;
            return;
        }

        IsRefreshingYak = true;
        try
        {
            YakMessage = await _backend.DownloadYakAsync(version);
        }
        catch (Exception e)
        {
            YakMessage = e.Message;
        }
        finally
        {
            IsRefreshingYak = false;
            RebuildYak();
        }
    }

    /// <summary>«Вернуть предыдущий» (FR-19): закрепить yak, использовавшийся до скачивания.</summary>
    [RelayCommand]
    private void RestorePreviousYak(YakGroupViewModel? group)
    {
        if (group is not { RhinoMajor: { } major, PreviousPath: { } path })
            return;
        (_backend.Settings.PinnedYak ??= [])[major] = path;
        _backend.Settings.PreviousYak?.Remove(major);
        _backend.Save();
        YakMessage = string.Format(CultureInfo.CurrentCulture, Strings.Set_YakRestored, major, path);
        RebuildYak();
    }

    [RelayCommand]
    private void OpenWizard() => _backend.OpenFirstRunWizard();

    [RelayCommand]
    private async Task RefreshYak()
    {
        IsRefreshingYak = true;
        try
        {
            await _backend.RefreshYakAsync();
        }
        catch (Exception e)
        {
            YakMessage = e.Message;
        }
        finally
        {
            IsRefreshingYak = false;
            RebuildYak();
        }
    }

    [RelayCommand]
    private void OpenToolFolder()
    {
        if (SelectedTool is { } t && System.IO.Path.GetDirectoryName(t.Path) is { } dir)
            _backend.OpenFolder(dir);
    }
}
