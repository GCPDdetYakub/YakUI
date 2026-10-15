using System.Globalization;
using YakUI.App.Resources;
using YakUI.Core.Install;
using YakUI.Core.Targets;

namespace YakUI.App.Services;

/// <summary>Исполнитель этапа 4: в приложении — фоновые операции над <see cref="PackageLifecycle"/>.</summary>
public interface ILifecycleBackend
{
    IReadOnlyDictionary<string, PinRecord> Pins(RhinoTarget target);

    /// <summary>Галочка автообновления пакетов Rhino (ОВ-19, 21.5).</summary>
    AutoUpdateState AutoUpdate(RhinoTarget target);

    Task<VersionChangeResult> ChangeVersionAsync(RhinoTarget target, string name, string? version);

    Task<IReadOnlyList<BulkItemResult>> UpdateAllAsync(RhinoTarget target, IReadOnlyCollection<string>? only);

    void Pin(RhinoTarget target, string name);

    void Unpin(RhinoTarget target, string name);
}

/// <summary>
/// Обновление, откат, закрепление и «Обновить всё» (FR-49, FR-58, FR-59) для экрана «Установленные» и карточки:
/// подтверждения (FR-85: прежняя версия удаляется), предупреждение об автообновлении Rhino при закреплении (ОВ-19),
/// итог одной строкой.
/// </summary>
public sealed class LifecycleCoordinator
{
    private readonly ILifecycleBackend _backend;
    private readonly IDialogService _dialogs;

    public LifecycleCoordinator(ILifecycleBackend backend, IDialogService dialogs)
    {
        _backend = backend;
        _dialogs = dialogs;
    }

    public ILifecycleBackend Backend => _backend;

    /// <summary>Текст для плашки «Установленных», если Rhino сам обновляет пакеты (ОВ-19); иначе null.</summary>
    public string? AutoUpdateText(RhinoTarget target) =>
        _backend.AutoUpdate(target) == AutoUpdateState.Enabled
            ? string.Format(CultureInfo.CurrentCulture, Strings.Lc_AutoUpdateOn, target.DisplayName)
            : null;

    /// <summary>Обновить (version = null — до самой новой подходящей) или откатить на выбранную версию.</summary>
    public async Task<UninstallOutcome?> ChangeVersionAsync(RhinoTarget target, string name, string? from, string? to)
    {
        var text = string.Format(CultureInfo.CurrentCulture, Strings.Lc_ConfirmChange, name, from ?? "—", to ?? Strings.Lc_Newest, target.DisplayName);
        if (from is not null)
            text += Environment.NewLine + string.Format(CultureInfo.CurrentCulture, Strings.Lc_OldRemoved, from);
        if (_backend.Pins(target).TryGetValue(name, out var pin))
            text += Environment.NewLine + string.Format(CultureInfo.CurrentCulture, Strings.Lc_PinMoves, pin.Version);
        if (!await _dialogs.ConfirmAsync(Strings.Lc_ChangeTitle, text, Strings.Lc_ChangeButton))
            return null;

        try
        {
            var r = await _backend.ChangeVersionAsync(target, name, to);
            var lines = new List<string>
            {
                string.Format(CultureInfo.CurrentCulture, Strings.Lc_Changed, r.Name, r.OldVersion ?? "—", r.NewVersion,
                    r.Method == InstallMethod.Yak ? Strings.Card_InstallMethodYak : Strings.Card_InstallMethodBuiltIn),
            };
            lines.AddRange(r.Warnings);
            if (r.PendingRestart)
                lines.Add(Strings.Card_InstallRestart);
            return new UninstallOutcome(true, string.Join(Environment.NewLine, lines));
        }
        catch (Exception e)
        {
            return new UninstallOutcome(false, string.Format(CultureInfo.CurrentCulture, Strings.Lc_ChangeFailed, e.Message));
        }
    }

    /// <summary>Закрепить активную версию; если Rhino сам обновляет пакеты — предупредить (ОВ-19).</summary>
    public async Task<UninstallOutcome?> PinAsync(RhinoTarget target, string name, string? version)
    {
        if (_backend.AutoUpdate(target) == AutoUpdateState.Enabled)
        {
            var warn = string.Format(CultureInfo.CurrentCulture, Strings.Lc_PinAutoUpdateWarning, name, version ?? "?", target.DisplayName);
            if (!await _dialogs.ConfirmAsync(Strings.Lc_PinTitle, warn, Strings.Lc_PinButton))
                return null;
        }

        try
        {
            _backend.Pin(target, name);
            return new UninstallOutcome(true, string.Format(CultureInfo.CurrentCulture, Strings.Lc_Pinned, name, version ?? "?"));
        }
        catch (Exception e)
        {
            return new UninstallOutcome(false, e.Message);
        }
    }

    public UninstallOutcome Unpin(RhinoTarget target, string name)
    {
        _backend.Unpin(target, name);
        return new UninstallOutcome(true, string.Format(CultureInfo.CurrentCulture, Strings.Lc_Unpinned, name));
    }

    /// <summary>«Обновить всё» или отмеченные (FR-59): одно подтверждение, затем по очереди; итог — сводка.</summary>
    public async Task<UninstallOutcome?> UpdateAllAsync(RhinoTarget target, IReadOnlyList<(string Name, string From, string To)> plan, bool selective)
    {
        if (plan.Count == 0)
            return new UninstallOutcome(true, Strings.Lc_NothingToUpdate);

        var pins = _backend.Pins(target);
        var list = string.Join(Environment.NewLine, plan.Select(p => pins.ContainsKey(p.Name)
            ? string.Format(CultureInfo.CurrentCulture, Strings.Lc_PlanPinned, p.Name, p.From)
            : string.Format(CultureInfo.CurrentCulture, Strings.Lc_PlanItem, p.Name, p.From, p.To)));
        var text = string.Format(CultureInfo.CurrentCulture, Strings.Lc_ConfirmAll, target.DisplayName) + Environment.NewLine + list;
        if (!await _dialogs.ConfirmAsync(Strings.Lc_AllTitle, text, Strings.Lc_AllButton))
            return null;

        try
        {
            var results = await _backend.UpdateAllAsync(target, selective ? plan.Select(p => p.Name).ToList() : null);
            return Summary(results);
        }
        catch (Exception e)
        {
            return new UninstallOutcome(false, string.Format(CultureInfo.CurrentCulture, Strings.Lc_ChangeFailed, e.Message));
        }
    }

    internal static UninstallOutcome Summary(IReadOnlyList<BulkItemResult> results)
    {
        var lines = new List<string>
        {
            string.Format(CultureInfo.CurrentCulture, Strings.Lc_Summary,
                results.Count(r => r.Outcome == BulkOutcome.Updated),
                results.Count(r => r.Outcome == BulkOutcome.Skipped),
                results.Count(r => r.Outcome == BulkOutcome.Failed)),
        };
        lines.AddRange(results.Select(r => r.Outcome switch
        {
            BulkOutcome.Updated => string.Format(CultureInfo.CurrentCulture, Strings.Lc_LineUpdated, r.Name, r.From, r.To) + (r.Message is null ? "" : " — " + r.Message),
            BulkOutcome.Skipped => string.Format(CultureInfo.CurrentCulture, Strings.Lc_LineSkipped, r.Name, r.Message),
            BulkOutcome.Cancelled => string.Format(CultureInfo.CurrentCulture, Strings.Lc_LineCancelled, r.Name),
            _ => string.Format(CultureInfo.CurrentCulture, Strings.Lc_LineFailed, r.Name, r.Message),
        }));
        return new UninstallOutcome(results.All(r => r.Outcome != BulkOutcome.Failed), string.Join(Environment.NewLine, lines));
    }
}
