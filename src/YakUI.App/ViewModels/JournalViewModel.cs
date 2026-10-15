using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YakUI.App.Resources;
using YakUI.App.Services;
using YakUI.Core.Logging;

namespace YakUI.App.ViewModels;

/// <summary>Данные экрана «Журнал».</summary>
public interface IJournalSource
{
    IReadOnlyList<JournalEntry> ReadLatest(int count);

    /// <summary>Папка текстовых логов (FR-90).</summary>
    string LogsDirectory { get; }

    bool OpenFolder(string path);
}

/// <summary>Строка журнала: время, вид, действие, результат, код, длительность; подробности — вывод yak (FR-16).</summary>
public sealed class JournalRowViewModel
{
    public JournalRowViewModel(JournalEntry e)
    {
        Entry = e;
        Time = e.Timestamp.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.CurrentCulture);
        CodeText = e.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "";
        DurationText = e.DurationMs is { } ms ? string.Format(CultureInfo.CurrentCulture, Strings.Jr_Ms, ms) : "";
        IsError = e.Result is "error" or "timeout" or "start-failed" or "yak-failed" or "failed"
                  || (e.ExitCode is { } code && code != 0);
    }

    public JournalEntry Entry { get; }

    public string Time { get; }

    public string Kind => Entry.Kind;

    public string Action => Entry.Action;

    public string Result => Entry.Result;

    public string CodeText { get; }

    public string DurationText { get; }

    public bool IsError { get; }

    /// <summary>Подробности читаемым текстом: поля JSON, stdout/stderr — как есть, многострочно.</summary>
    public string DetailsText => FormatDetails(Entry);

    internal static string FormatDetails(JournalEntry e)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.CurrentCulture, $"{e.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}  {e.UserName}");
        sb.AppendLine(CultureInfo.CurrentCulture, $"{e.Kind}: {e.Action} → {e.Result}");
        if (e.Details is not { Length: > 0 } json)
            return sb.ToString();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                sb.AppendLine(json);
                return sb.ToString();
            }

            foreach (var p in doc.RootElement.EnumerateObject())
            {
                switch (p.Value.ValueKind)
                {
                    case JsonValueKind.String when p.Value.GetString() is { } s && s.Contains('\n'):
                        sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"{p.Name}:").AppendLine(s.TrimEnd());
                        break;
                    case JsonValueKind.String:
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{p.Name}: {p.Value.GetString()}");
                        break;
                    case JsonValueKind.Null:
                        break;
                    default:
                        sb.AppendLine(CultureInfo.InvariantCulture, $"{p.Name}: {p.Value.GetRawText()}");
                        break;
                }
            }
        }
        catch (JsonException)
        {
            sb.AppendLine(json);
        }

        return sb.ToString();
    }
}

/// <summary>Экран «Журнал» (FR-90, FR-16): последние операции с фильтром по виду и тексту, подробности, папка логов.</summary>
public sealed partial class JournalViewModel : ScreenViewModel
{
    public const int PageSize = 1000;

    private readonly IJournalSource _source;
    private readonly IUiDispatcher _ui;
    private List<JournalRowViewModel> _all = [];

    public JournalViewModel(IJournalSource source, IUiDispatcher ui)
        : base(ScreenKind.Journal, Strings.Nav_Journal)
    {
        _source = source;
        _ui = ui;
    }

    [ObservableProperty]
    private ObservableCollection<JournalRowViewModel> _rows = [];

    [ObservableProperty]
    private JournalRowViewModel? _selectedRow;

    [ObservableProperty]
    private ObservableCollection<string> _kinds = [];

    [ObservableProperty]
    private string _selectedKind = "";

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private bool _onlyErrors;

    [ObservableProperty]
    private string _countText = "";

    [ObservableProperty]
    private string? _message;

    internal Task LastRefresh { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    public void Refresh()
    {
        LastRefresh = Task.Run(() =>
        {
            List<JournalRowViewModel> rows;
            string? error = null;
            try
            {
                rows = _source.ReadLatest(PageSize).Select(e => new JournalRowViewModel(e)).ToList();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                rows = [];
                error = e.Message;
            }

            _ui.Post(() =>
            {
                _all = rows;
                var selectedKind = SelectedKind;
                Kinds = new ObservableCollection<string>([Strings.Jr_AllKinds, .. rows.Select(r => r.Kind).Distinct().Order(StringComparer.Ordinal)]);
                SelectedKind = Kinds.Contains(selectedKind) ? selectedKind : Kinds[0];
                Message = error is null ? null : string.Format(CultureInfo.CurrentCulture, Strings.Jr_ReadFailed, error);
                ApplyFilter();
            });
        });
    }

    [RelayCommand]
    private void OpenLogs() => _source.OpenFolder(_source.LogsDirectory);

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnSelectedKindChanged(string value) => ApplyFilter();

    partial void OnOnlyErrorsChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        var f = FilterText.Trim();
        var kind = SelectedKind == Strings.Jr_AllKinds ? null : SelectedKind;
        var visible = _all
            .Where(r => kind is null || r.Kind == kind)
            .Where(r => !OnlyErrors || r.IsError)
            .Where(r => f.Length == 0
                        || r.Action.Contains(f, StringComparison.OrdinalIgnoreCase)
                        || r.Result.Contains(f, StringComparison.OrdinalIgnoreCase)
                        || (r.Entry.Details?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
        var selectedId = SelectedRow?.Entry.Id;
        Rows = new ObservableCollection<JournalRowViewModel>(visible);
        SelectedRow = selectedId is null ? null : Rows.FirstOrDefault(r => r.Entry.Id == selectedId);
        CountText = string.Format(CultureInfo.CurrentCulture, Strings.Jr_Count, visible.Count, _all.Count);
        if (_all.Count == 0 && Message is null)
            Message = Strings.Jr_Empty;
    }
}
