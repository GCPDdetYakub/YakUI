using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YakUI.App.Resources;
using YakUI.App.Services;
using YakUI.Core.Downloads;

namespace YakUI.App.ViewModels;

/// <summary>Данные экрана «Загрузки»: очередь, реестр, файловые действия.</summary>
public interface IDownloadsSource
{
    DownloadQueue Queue { get; }

    string Directory { get; }

    IReadOnlyList<DownloadRecord> Library();

    /// <summary>Дата публикации версии: из реестра, для старых записей — из каталога (FR-37b).</summary>
    DateTimeOffset? PublishedAt(DownloadRecord record);

    void Forget(DownloadRecord record);

    bool OpenFolder(string path);

    bool Reveal(string file);
}

/// <summary>Строка очереди (FR-38).</summary>
public sealed partial class DownloadItemViewModel : ObservableObject
{
    private readonly DownloadQueue _queue;

    public DownloadItemViewModel(DownloadItem item, DownloadQueue queue)
    {
        Item = item;
        _queue = queue;
        Update();
    }

    public DownloadItem Item { get; }

    public string Filename => Item.Request.Filename;

    public string? PackageText => Item.Request.Package is { } p ? $"{p} {Item.Request.Version}" : null;

    [ObservableProperty]
    private string _stateText = "";

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private bool _isIndeterminate;

    [ObservableProperty]
    private string _sizeText = "";

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _canCancel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    private bool _canRetry;

    [ObservableProperty]
    private bool _isFailed;

    internal void Update()
    {
        StateText = Item.State switch
        {
            DownloadState.Queued => Strings.Dl_State_Queued,
            DownloadState.Running => Strings.Dl_State_Running,
            DownloadState.Verifying => Strings.Dl_State_Verifying,
            DownloadState.Completed => Strings.Dl_State_Completed,
            DownloadState.AlreadyDownloaded => Strings.Dl_State_Already,
            DownloadState.Failed => Strings.Dl_State_Failed,
            _ => Strings.Dl_State_Cancelled,
        };
        IsIndeterminate = Item.State is DownloadState.Queued or DownloadState.Verifying || (Item.State == DownloadState.Running && Item.BytesTotal is null);
        Percent = Item.BytesTotal is > 0 ? 100.0 * Item.BytesDone / Item.BytesTotal.Value : Item.IsFinished ? 100 : 0;
        SizeText = Item.BytesTotal is { } total
            ? $"{DistributionRowViewModel.FormatSize(Item.BytesDone)} / {DistributionRowViewModel.FormatSize(total)}"
            : Item.BytesDone > 0 ? DistributionRowViewModel.FormatSize(Item.BytesDone) : "";
        Error = Item.Error;
        IsFailed = Item.State == DownloadState.Failed;
        CanCancel = !Item.IsFinished;
        CanRetry = Item.CanRetry;
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _queue.Cancel(Item);

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void Retry() => _queue.Retry(Item);
}

/// <summary>Файл библиотеки загрузок (FR-35, FR-37).</summary>
public sealed class LibraryRowViewModel
{
    public LibraryRowViewModel(DownloadRecord record, DateTimeOffset? publishedAt = null)
    {
        Record = record;
        Exists = record.FileExists;
        var published = record.PublishedAt ?? publishedAt;
        PublishedText = published is { } p ? p.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.CurrentCulture) : "—";
    }

    /// <summary>FR-37b (v0.2.15): основная дата — публикация версии.</summary>
    public string PublishedText { get; }

    public DownloadRecord Record { get; }

    public string Filename => Record.Filename;

    public string PackageText => Record.Package is { } p ? $"{p} {Record.Version}" : "";

    public string? Tag => Record.Tag;

    public string Size => DistributionRowViewModel.FormatSize(Record.Size);

    /// <summary>Когда скачан.</summary>
    public string Date => string.Format(CultureInfo.CurrentCulture, Strings.Dl_Downloaded, Record.CompletedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture));

    /// <summary>FR-37b: подсказка «дата файла на сервере» (Last-Modified); у файлов, скачанных до v7, — «—».</summary>
    public string ServerDate => Record.ServerModifiedAt is { } d
        ? string.Format(CultureInfo.CurrentCulture, Strings.Dl_ServerDate, d.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture))
        : Strings.Dl_ServerDateUnknown;

    public string ShortSha => Record.Sha256.Length > 12 ? Record.Sha256[..12] + "…" : Record.Sha256;

    public bool Exists { get; }

    public string? MissingText => Exists ? null : Strings.Dl_Missing;
}

/// <summary>
/// Экран «Загрузки» (10, FR-35…FR-38): очередь с прогрессом, отменой и повтором; библиотека скачанных .yak
/// с SHA-256; «показать в папке» и «удалить файл» с подтверждением (FR-85). Очистка по N последних (FR-39) — позже.
/// </summary>
public sealed partial class DownloadsViewModel : ScreenViewModel
{
    private readonly IDownloadsSource _source;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly Dictionary<DownloadItem, DownloadItemViewModel> _byItem = [];

    public DownloadsViewModel(IDownloadsSource source, IUiDispatcher ui, IDialogService dialogs)
        : base(ScreenKind.Downloads, Strings.Nav_Downloads)
    {
        _source = source;
        _ui = ui;
        _dialogs = dialogs;
        _folderText = string.Format(CultureInfo.CurrentCulture, Strings.Dl_Folder, source.Directory);
        _source.Queue.Changed += item => _ui.Post(() => OnItemChanged(item));
        foreach (var item in _source.Queue.Items)
            OnItemChanged(item);
        RefreshLibrary();
    }

    [ObservableProperty]
    private string _folderText;

    /// <summary>Папка загрузок изменена в «Настройках» (FR-35).</summary>
    public void Reload()
    {
        FolderText = string.Format(CultureInfo.CurrentCulture, Strings.Dl_Folder, _source.Directory);
        RefreshLibrary();
    }

    public ObservableCollection<DownloadItemViewModel> Queue { get; } = [];

    [ObservableProperty]
    private ObservableCollection<LibraryRowViewModel> _library = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevealCommand), nameof(DeleteCommand))]
    private LibraryRowViewModel? _selectedFile;

    [ObservableProperty]
    private bool _isQueueEmpty = true;

    [ObservableProperty]
    private bool _isLibraryEmpty = true;

    private void OnItemChanged(DownloadItem item)
    {
        if (!_byItem.TryGetValue(item, out var vm))
        {
            vm = new DownloadItemViewModel(item, _source.Queue);
            _byItem[item] = vm;
            Queue.Insert(0, vm);
        }

        vm.Update();
        IsQueueEmpty = Queue.Count == 0;
        if (item.State is DownloadState.Completed or DownloadState.AlreadyDownloaded)
            RefreshLibrary();
    }

    [RelayCommand]
    public void RefreshLibrary()
    {
        var selected = SelectedFile?.Record.Id;
        Library = new ObservableCollection<LibraryRowViewModel>(_source.Library().Select(r => new LibraryRowViewModel(r, _source.PublishedAt(r))));
        SelectedFile = Library.FirstOrDefault(r => r.Record.Id == selected);
        IsLibraryEmpty = Library.Count == 0;
    }

    [RelayCommand]
    private void ClearFinished()
    {
        _source.Queue.ClearFinished();
        foreach (var vm in Queue.Where(q => q.Item.IsFinished).ToList())
        {
            Queue.Remove(vm);
            _byItem.Remove(vm.Item);
        }

        IsQueueEmpty = Queue.Count == 0;
    }

    [RelayCommand]
    private void OpenFolder() => _source.OpenFolder(_source.Directory);

    private bool HasFile() => SelectedFile is not null;

    [RelayCommand(CanExecute = nameof(HasFile))]
    private void Reveal()
    {
        if (SelectedFile is { } f)
            _source.Reveal(f.Record.Path);
    }

    /// <summary>Удалить файл из библиотеки — только после подтверждения (FR-85). Удаляется только сам файл в папке загрузок.</summary>
    [RelayCommand(CanExecute = nameof(HasFile))]
    private async Task DeleteAsync()
    {
        if (SelectedFile is not { } f)
            return;
        var ok = await _dialogs.ConfirmAsync(Strings.Dl_DeleteTitle, string.Format(CultureInfo.CurrentCulture, Strings.Dl_DeleteConfirm, f.Filename), Strings.Dl_Delete);
        if (!ok)
            return;
        _source.Forget(f.Record);
        RefreshLibrary();
    }
}
