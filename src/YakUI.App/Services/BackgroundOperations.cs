using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YakUI.App.Resources;
using YakUI.Core.Logging;

namespace YakUI.App.Services;

public enum OperationState
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>Контекст фоновой операции: отмена и отчёт о прогрессе (FR-84).</summary>
public sealed class OperationContext
{
    private readonly BackgroundOperation _op;

    internal OperationContext(BackgroundOperation op, CancellationToken token)
    {
        _op = op;
        Token = token;
    }

    public CancellationToken Token { get; }

    /// <summary>Прогресс 0…1; null — неопределённый.</summary>
    public void Report(double? fraction, string? detail = null) => _op.Update(fraction, detail);
}

/// <summary>Одна фоновая операция в строке состояния.</summary>
public sealed partial class BackgroundOperation : ObservableObject
{
    private readonly CancellationTokenSource _cts = new();
    private readonly IUiDispatcher _ui;
    private readonly Action<BackgroundOperation> _remove;

    internal BackgroundOperation(string title, bool cancellable, IUiDispatcher ui, Action<BackgroundOperation> remove)
    {
        Title = title;
        CanCancel = cancellable;
        _ui = ui;
        _remove = remove;
    }

    public string Title { get; }

    public bool CanCancel { get; }

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isIndeterminate = true;

    [ObservableProperty]
    private string? _detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsFinishedWithMessage))]
    private OperationState _state = OperationState.Running;

    public bool IsRunning => State == OperationState.Running;

    public bool IsFinishedWithMessage => State is OperationState.Failed or OperationState.Cancelled;

    internal CancellationToken Token => _cts.Token;

    internal void Update(double? fraction, string? detail) => _ui.Post(() =>
    {
        IsIndeterminate = fraction is null;
        Progress = Math.Clamp(fraction ?? 0, 0, 1) * 100;
        if (detail is not null)
            Detail = detail;
    });

    internal void Finish(OperationState state, string? detail) => _ui.Post(() =>
    {
        State = state;
        if (detail is not null)
            Detail = detail;
        if (state == OperationState.Succeeded)
            _remove(this);
    });

    [RelayCommand]
    private void Cancel()
    {
        if (CanCancel && IsRunning)
            _cts.Cancel();
    }

    [RelayCommand]
    private void Dismiss() => _remove(this);
}

/// <summary>
/// Запуск долгих операций в фоне с отменой и прогрессом; UI не блокируется (FR-84).
/// Успешные операции исчезают из строки состояния, ошибки и отмены остаются до «Скрыть». Всё пишется в текстовый лог.
/// </summary>
public sealed class BackgroundOperations
{
    private readonly IUiDispatcher _ui;
    private readonly IAppLog _log;

    public BackgroundOperations(IUiDispatcher ui, IAppLog log)
    {
        _ui = ui;
        _log = log;
    }

    public ObservableCollection<BackgroundOperation> Items { get; } = [];

    private int _running;

    /// <summary>Число выполняющихся операций (FR-19: переключение yak — когда других нет).</summary>
    public int RunningCount => Volatile.Read(ref _running);

    public Task RunAsync(string title, Func<OperationContext, Task> work, bool cancellable = true)
    {
        var op = new BackgroundOperation(title, cancellable, _ui, o => _ui.Post(() => Items.Remove(o)));
        _ui.Post(() => Items.Add(op));
        _log.Info($"Операция начата: {title}");

        Interlocked.Increment(ref _running);
        return Task.Run(async () =>
        {
            try
            {
                await work(new OperationContext(op, op.Token)).ConfigureAwait(false);
                op.Finish(OperationState.Succeeded, null);
                _log.Info($"Операция завершена: {title}");
            }
            catch (OperationCanceledException) when (op.Token.IsCancellationRequested)
            {
                op.Finish(OperationState.Cancelled, Strings.Status_Cancelled);
                _log.Info($"Операция отменена: {title}");
            }
            catch (Exception e)
            {
                op.Finish(OperationState.Failed, string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Status_Failed, e.Message));
                _log.Error($"Операция завершилась ошибкой: {title}", e);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        });
    }
}
