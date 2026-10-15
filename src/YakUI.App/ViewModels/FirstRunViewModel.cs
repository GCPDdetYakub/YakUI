using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YakUI.App.Resources;
using YakUI.Core.Targets;
using YakUI.Core.Tools;

namespace YakUI.App.ViewModels;

/// <summary>Что нужно мастеру первого запуска (в приложении — YakService и библиотека yak).</summary>
public interface IFirstRunBackend
{
    IReadOnlyList<RhinoTarget> Targets { get; }

    IReadOnlyList<YakTool> Tools { get; }

    YakTool? SelectFor(int rhinoMajor);

    YakCheckResult? LastCheck { get; }

    Task<YakCheckResult> CheckAsync();

    Task<string> DownloadAsync(string version);

    /// <summary>Мастер пройден или пропущен — больше не показывать при запуске.</summary>
    void MarkDone();
}

/// <summary>Строка мастера: версия Rhino, её встроенный yak, более новая сборка той же линейки (FR-18).</summary>
public sealed partial class FirstRunRowViewModel : ObservableObject
{
    public FirstRunRowViewModel(RhinoTarget target, string yakText, string? noYakText, string? availableVersion)
    {
        Target = target;
        YakText = yakText;
        NoYakText = noYakText;
        AvailableVersion = availableVersion;
        _download = availableVersion is not null;
    }

    public RhinoTarget Target { get; }

    public string Title => Target.DisplayName;

    public string YakText { get; }

    /// <summary>Чего не будет без пригодного yak (AD-10).</summary>
    public string? NoYakText { get; }

    public string? AvailableVersion { get; }

    public string? AvailableText => AvailableVersion is { } v ? string.Format(CultureInfo.CurrentCulture, Strings.Wiz_Available, v) : null;

    public bool HasAvailable => AvailableVersion is not null;

    [ObservableProperty]
    private bool _download;
}

/// <summary>
/// Мастер первого запуска (FR-18): по каждой установленной версии Rhino — встроенный yak (версия, привязка, подпись),
/// более новая сборка той же линейки с files.mcneel.com с предложением скачать, и что недоступно без yak (AD-10).
/// Пропуск допустим; повторно открывается из «Настроек».
/// </summary>
public sealed partial class FirstRunViewModel : ObservableObject
{
    private readonly IFirstRunBackend _backend;

    public FirstRunViewModel(IFirstRunBackend backend)
    {
        _backend = backend;
        Rebuild();
    }

    public event Action? CloseRequested;

    [ObservableProperty]
    private ObservableCollection<FirstRunRowViewModel> _rows = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand), nameof(SkipCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _status;

    public bool NoRhino => Rows.Count == 0;

    internal Task LastLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Проверка новых сборок, если её ещё не было.</summary>
    public Task LoadAsync()
    {
        if (_backend.LastCheck is not null)
            return Task.CompletedTask;
        return LastLoad = Check();
    }

    private async Task Check()
    {
        IsBusy = true;
        Status = Strings.Wiz_Checking;
        try
        {
            var r = await _backend.CheckAsync();
            Status = r.Problem;
        }
        catch (Exception e)
        {
            Status = string.Format(CultureInfo.CurrentCulture, Strings.Wiz_CheckFailed, e.Message);
        }
        finally
        {
            IsBusy = false;
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var check = _backend.LastCheck;
        Rows = new ObservableCollection<FirstRunRowViewModel>(_backend.Targets.Select(t =>
        {
            var builtIn = _backend.Tools.FirstOrDefault(x => x.Origin == YakToolOrigin.BuiltIn && string.Equals(x.Path, t.BuiltInYakPath, StringComparison.OrdinalIgnoreCase));
            var yakText = builtIn is null
                ? Strings.Wiz_NoBuiltIn
                : string.Format(CultureInfo.CurrentCulture, Strings.Wiz_BuiltIn, builtIn.Version?.ToString() ?? "?",
                    builtIn.BoundRhinoMajor?.ToString(CultureInfo.InvariantCulture) ?? "?",
                    builtIn.Trust == YakTrust.SignedByMcNeel ? Strings.Wiz_SignedMcNeel : Strings.Wiz_NotSigned)
                  + (builtIn.Problem is { } p ? " " + string.Format(CultureInfo.CurrentCulture, Strings.Set_YakUnusable, p) : "");
            var usable = _backend.SelectFor(t.Major);
            var newer = check?.Lines.FirstOrDefault(l => l.RhinoMajor == t.Major)?.Newer?.Version.ToString();
            return new FirstRunRowViewModel(t, yakText, usable is null ? Strings.Wiz_NoYak : null, newer);
        }));
        OnPropertyChanged(nameof(NoRhino));
    }

    private bool CanAct() => !IsBusy;

    /// <summary>Скачать отмеченные сборки и закрыть.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task Finish()
    {
        IsBusy = true;
        var messages = new List<string>();
        try
        {
            foreach (var v in Rows.Where(r => r.Download && r.AvailableVersion is not null).Select(r => r.AvailableVersion!).Distinct())
            {
                Status = string.Format(CultureInfo.CurrentCulture, Strings.Op_YakDownload, v);
                try
                {
                    messages.Add(await _backend.DownloadAsync(v));
                }
                catch (Exception e)
                {
                    messages.Add(e.Message);
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        _backend.MarkDone();
        Status = messages.Count == 0 ? null : string.Join(Environment.NewLine, messages);
        CloseRequested?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Skip()
    {
        _backend.MarkDone();
        CloseRequested?.Invoke();
    }
}
