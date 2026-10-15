using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YakUI.App.Resources;
using YakUI.App.Services;
using YakUI.Core.Hosts;
using YakUI.Core.Logging;
using YakUI.Core.Platform;
using YakUI.Core.Settings;

namespace YakUI.App.ViewModels;

/// <summary>Выбор в диалоге «Открыть с помощью» (FR-88): редактор и «Запомнить выбор».</summary>
public sealed record EditorChoice(TextEditor Editor, bool Remember);

/// <summary>Диалоги hosts: собственный «Открыть с помощью» (FR-88a).</summary>
public interface IHostsDialogs
{
    Task<EditorChoice?> ChooseEditorAsync(IReadOnlyList<TextEditor> editors);
}

/// <summary>Строка индикатора контрольных строк (FR-89c).</summary>
public sealed record HostsStatusItem(string Text, HostsLineState State, string Tooltip);

/// <summary>
/// «Открыть hosts» (14.2): выбор и запоминание редактора (FR-88, FR-89), запуск с правами администратора (FR-89a),
/// копия на сеанс (FR-89b), возврат изменений при закрытии и после сбоя (FR-89e, FR-89f), индикатор контрольных
/// строк (FR-89c, FR-89g) и их список в «Настройках» (FR-89h). Действия пишутся в журнал (FR-89d).
/// </summary>
public sealed partial class HostsViewModel : ObservableObject
{
    private readonly IHostsPlatform _platform;
    private readonly HostsSession _session;
    private readonly AppSettings _settings;
    private readonly Action _save;
    private readonly IDialogService _dialogs;
    private readonly IHostsDialogs _hostsDialogs;
    private readonly IUiDispatcher _ui;
    private readonly OperationJournal? _journal;
    private readonly List<Task> _editors = [];

    public HostsViewModel(IHostsPlatform platform, HostsSession session, AppSettings settings, Action save, IDialogService dialogs,
        IHostsDialogs hostsDialogs, IUiDispatcher ui, OperationJournal? journal = null)
    {
        _platform = platform;
        _session = session;
        _settings = settings;
        _save = save;
        _dialogs = dialogs;
        _hostsDialogs = hostsDialogs;
        _ui = ui;
        _journal = journal;
        WatchList = new ObservableCollection<string>(settings.HostsWatchList ?? []);
        UpdateEditorText();
        Refresh();
    }

    // ---- Индикатор (FR-89c, FR-89g) ----

    [ObservableProperty]
    private IReadOnlyList<HostsStatusItem> _statuses = [];

    [ObservableProperty]
    private bool _isIndicatorVisible;

    [ObservableProperty]
    private string _indicatorText = "";

    [ObservableProperty]
    private string _indicatorTooltip = "";

    /// <summary>Перечитать hosts и обновить индикатор; список пуст — индикатор скрыт.</summary>
    public void Refresh()
    {
        var watched = HostsAnalyzer.ParseList(_settings.HostsWatchList);
        if (watched.Count == 0)
        {
            Statuses = [];
            IsIndicatorVisible = false;
            return;
        }

        string content;
        try
        {
            content = File.ReadAllText(_platform.HostsPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            content = "";
        }

        var result = HostsAnalyzer.Analyze(content, watched);
        Statuses = result.Select(r => new HostsStatusItem(r.Entry.ToString(), r.State, Describe(r))).ToList();
        var active = result.Count(r => r.State == HostsLineState.Active);
        var commented = result.Count(r => r.State == HostsLineState.Commented);
        var missing = result.Count(r => r.State == HostsLineState.Missing);
        IndicatorText = string.Format(CultureInfo.CurrentCulture, Strings.Hosts_Indicator, active, commented, missing);
        IndicatorTooltip = string.Join(Environment.NewLine, Statuses.Select(s => s.Tooltip));
        IsIndicatorVisible = true;
    }

    private static string Describe(HostsLineStatus s) => s.State switch
    {
        HostsLineState.Active => string.Format(CultureInfo.CurrentCulture, Strings.Hosts_LineActive, s.Entry, s.LineNumber),
        HostsLineState.Commented => string.Format(CultureInfo.CurrentCulture, Strings.Hosts_LineCommented, s.Entry, s.LineNumber),
        _ => string.Format(CultureInfo.CurrentCulture, Strings.Hosts_LineMissing, s.Entry),
    };

    /// <summary>Наблюдатель за папкой hosts (FR-89g) вызывает из своего потока.</summary>
    public void OnHostsChanged() => _ui.Post(Refresh);

    // ---- «Открыть hosts» (FR-87…FR-89b) ----

    [ObservableProperty]
    private string? _message;

    [RelayCommand]
    private async Task OpenHostsAsync()
    {
        Message = null;
        var editor = SavedEditor();
        if (editor is null)
        {
            var choice = await _hostsDialogs.ChooseEditorAsync(_platform.FindEditors());
            if (choice is null)
                return;
            editor = choice.Editor;
            if (choice.Remember)
            {
                _settings.HostsEditor = new HostsEditorSetting(editor.Path, editor.Arguments);
                _save();
                UpdateEditorText();
            }
        }

        try
        {
            _session.EnsureBackup(); // FR-89b: одна копия на сеанс
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = string.Format(CultureInfo.CurrentCulture, Strings.Hosts_BackupFailed, e.Message);
            _journal?.Write("hosts", "backup", "error", details: new { error = e.Message });
            return;
        }

        var launch = _platform.OpenEditor(editor, _platform.HostsPath, elevated: true);
        if (launch.Result == ElevationResult.Declined)
        {
            _journal?.Write("hosts", "open", "uac-declined", details: new { editor = editor.Path });
            if (!await _dialogs.ConfirmAsync(Strings.Hosts_Title, Strings.Hosts_ViewOnlyConfirm, Strings.Hosts_ViewOnlyButton))
                return;
            launch = _platform.OpenEditor(editor, _platform.HostsPath, elevated: false);
            _journal?.Write("hosts", "open", launch.Result == ElevationResult.Ok ? "view-only" : "error", details: new { editor = editor.Path });
        }
        else
        {
            _journal?.Write("hosts", "open", launch.Result == ElevationResult.Ok ? "ok" : "error",
                details: new { editor = editor.Path, arguments = editor.Arguments, error = launch.Error });
        }

        if (launch.Result == ElevationResult.Failed)
        {
            Message = string.Format(CultureInfo.CurrentCulture, Strings.Hosts_OpenFailed, launch.Error);
            return;
        }

        lock (_editors)
            _editors.Add(launch.Exited);
        _ = launch.Exited.ContinueWith(_ => _ui.Post(Refresh), TaskScheduler.Default); // FR-89g: после закрытия редактора
    }

    private TextEditor? SavedEditor()
    {
        var saved = _settings.HostsEditor;
        if (saved is null)
            return null;
        if (File.Exists(saved.Path))
            return new TextEditor(Path.GetFileNameWithoutExtension(saved.Path), saved.Path, saved.Arguments);
        _journal?.Write("hosts", "editor-missing", "ask", details: new { saved.Path }); // FR-88: редактора больше нет — спросить снова
        return null;
    }

    private bool EditorStillOpen()
    {
        lock (_editors)
            return _editors.Any(t => !t.IsCompleted);
    }

    // ---- Возврат изменений (FR-89e, FR-89f) ----

    /// <summary>При запуске: отметка осталась от сбоя или отказа от UAC — сообщить и восстановить.</summary>
    public async Task CheckPendingRestoreAsync()
    {
        if (!_session.IsOpen || _session.BackedUpThisSession)
            return;
        if (!_session.HasChanges())
        {
            _session.Close("unchanged");
            return;
        }

        if (await _dialogs.ConfirmAsync(Strings.Hosts_Title, Strings.Hosts_NotRestored, Strings.Hosts_RestoreButton))
            await RestoreAsync();
    }

    /// <summary>При закрытии YakUI: hosts изменён в этом сеансе — вернуть из копии (один запрос UAC).</summary>
    public async Task OnClosingAsync()
    {
        if (!_session.IsOpen)
            return;
        if (!_session.HasChanges())
        {
            _session.Close("unchanged");
            return;
        }

        if (EditorStillOpen())
            await _dialogs.ConfirmAsync(Strings.Hosts_Title, Strings.Hosts_CloseEditor, Strings.Hosts_RestoreButton);
        await RestoreAsync();
    }

    private async Task RestoreAsync()
    {
        var result = await _platform.RestoreElevatedAsync(_session.BackupPath);
        _journal?.Write("hosts", "restore", result.ToString().ToLowerInvariant());
        if (result == ElevationResult.Ok)
            _session.Close("restored"); // отказ или ошибка — отметка остаётся до следующего запуска (FR-89f)
        _ui.Post(Refresh);
    }

    // ---- Настройки: редактор (FR-89) ----

    [ObservableProperty]
    private string _editorText = "";

    private void UpdateEditorText() =>
        EditorText = _settings.HostsEditor is { } e ? (string.IsNullOrWhiteSpace(e.Arguments) ? e.Path : $"{e.Path} {e.Arguments}") : Strings.Hosts_EditorNotChosen;

    [RelayCommand]
    private async Task ChangeEditorAsync()
    {
        var choice = await _hostsDialogs.ChooseEditorAsync(_platform.FindEditors());
        if (choice is null)
            return;
        _settings.HostsEditor = new HostsEditorSetting(choice.Editor.Path, choice.Editor.Arguments);
        _save();
        UpdateEditorText();
        _journal?.Write("hosts", "editor", "chosen", details: new { choice.Editor.Path, choice.Editor.Arguments });
    }

    [RelayCommand]
    private void ResetEditor()
    {
        _settings.HostsEditor = null;
        _save();
        UpdateEditorText();
    }

    // ---- Настройки: контрольные строки (FR-89h) ----

    public ObservableCollection<string> WatchList { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReplaceLineCommand), nameof(RemoveLineCommand), nameof(MoveUpCommand), nameof(MoveDownCommand))]
    private string? _selectedLine;

    [ObservableProperty]
    private string _lineInput = "";

    [ObservableProperty]
    private string? _lineError;

    partial void OnSelectedLineChanged(string? value)
    {
        if (value is not null)
            LineInput = value;
    }

    [RelayCommand]
    private void AddLine()
    {
        if (Validate(null) is { } entry)
        {
            WatchList.Add(entry);
            LineInput = "";
            SaveList();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ReplaceLine()
    {
        var index = SelectedLine is null ? -1 : WatchList.IndexOf(SelectedLine);
        if (index >= 0 && Validate(index) is { } entry)
        {
            WatchList[index] = entry;
            SelectedLine = entry;
            SaveList();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveLine()
    {
        if (SelectedLine is { } line)
        {
            WatchList.Remove(line);
            LineInput = "";
            SaveList();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveDown() => Move(1);

    private bool HasSelection() => SelectedLine is not null;

    private void Move(int delta)
    {
        if (SelectedLine is not { } line)
            return;
        var i = WatchList.IndexOf(line);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= WatchList.Count)
            return;
        WatchList.Move(i, j);
        SelectedLine = line;
        SaveList();
    }

    /// <summary>Корректный IPv4/IPv6 и имя хоста, без дубликатов (кроме заменяемой строки).</summary>
    private string? Validate(int? replacing)
    {
        if (!HostsEntry.TryParse(LineInput, out var entry, out var error))
        {
            LineError = error;
            return null;
        }

        var text = entry!.ToString();
        for (var i = 0; i < WatchList.Count; i++)
        {
            if (i != replacing && HostsEntry.TryParse(WatchList[i], out var other, out _) && other!.Matches(entry.Ip, entry.Host))
            {
                LineError = string.Format(CultureInfo.CurrentCulture, Strings.Hosts_Duplicate, text);
                return null;
            }
        }

        LineError = null;
        return text;
    }

    private void SaveList()
    {
        _settings.HostsWatchList = [.. WatchList];
        _save();
        Refresh();
    }
}
