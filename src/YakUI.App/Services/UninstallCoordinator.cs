using System.Globalization;
using YakUI.App.Resources;
using YakUI.Core.Install;
using YakUI.Core.Targets;

namespace YakUI.App.Services;

/// <summary>Исполнитель удаления: в приложении — фоновая операция над <see cref="StandardUninstaller"/>.</summary>
public interface IUninstallBackend
{
    UninstallPreview Preview(RhinoTarget target, string name);

    Task<UninstallResult> UninstallAsync(RhinoTarget target, string name);
}

/// <summary>Итог «Удалить»: успех и текст для показа.</summary>
public sealed record UninstallOutcome(bool Succeeded, string Message);

/// <summary>
/// «Удалить» пакет из цели целиком (FR-48) для экрана «Установленные» и карточки: подтверждение (FR-85) с перечнем
/// всех версий и предупреждением о запущенном Rhino (FR-05), затем удаление; возвращает итог или null при отказе.
/// </summary>
public sealed class UninstallCoordinator
{
    private readonly IUninstallBackend _backend;
    private readonly IDialogService _dialogs;

    public UninstallCoordinator(IUninstallBackend backend, IDialogService dialogs)
    {
        _backend = backend;
        _dialogs = dialogs;
    }

    public async Task<UninstallOutcome?> UninstallAsync(RhinoTarget target, string name)
    {
        UninstallPreview preview;
        try
        {
            preview = _backend.Preview(target, name);
        }
        catch (Exception e)
        {
            return Failed(e.Message);
        }

        if (preview.BlockedReason is { } blocked)
            return Failed(blocked);

        var message = ConfirmText(preview, target);
        if (!await _dialogs.ConfirmAsync(Strings.Un_Title, message, Strings.Un_Button))
            return null;

        try
        {
            var r = await _backend.UninstallAsync(target, name);
            var text = string.Format(CultureInfo.CurrentCulture, Strings.Un_Done, r.Name, target.DisplayName,
                r.Method == InstallMethod.Yak ? Strings.Card_InstallMethodYak : Strings.Un_MethodBuiltIn);
            return new UninstallOutcome(true, r.PendingRestart ? text + Environment.NewLine + Strings.Un_Restart : text);
        }
        catch (Exception e)
        {
            return Failed(e.Message);
        }
    }

    /// <summary>Удалить отмеченные пакеты цели (FR-59): одно подтверждение со всеми версиями, затем по очереди.</summary>
    public async Task<UninstallOutcome?> UninstallManyAsync(RhinoTarget target, IReadOnlyList<string> names)
    {
        var previews = new List<UninstallPreview>();
        var blocked = new List<string>();
        foreach (var n in names)
        {
            var p = _backend.Preview(target, n);
            if (p.BlockedReason is { } why)
                blocked.Add($"{n}: {why}");
            else
                previews.Add(p);
        }

        if (previews.Count == 0)
            return Failed(string.Join(" ", blocked));

        var text = string.Format(CultureInfo.CurrentCulture, Strings.Un_ConfirmMany, previews.Count, target.DisplayName) + Environment.NewLine
            + string.Join(Environment.NewLine, previews.Select(p => $"• {p.Name}: {string.Join(", ", p.Versions)}"));
        if (previews.Any(p => p.RhinoRunning))
            text += Environment.NewLine + string.Format(CultureInfo.CurrentCulture, Strings.Un_Running, target.DisplayName);
        if (blocked.Count > 0)
            text += Environment.NewLine + string.Join(Environment.NewLine, blocked);
        if (!await _dialogs.ConfirmAsync(Strings.Un_Title, text, Strings.Un_Button))
            return null;

        var lines = new List<string>();
        var ok = true;
        foreach (var p in previews)
        {
            try
            {
                var r = await _backend.UninstallAsync(target, p.Name);
                lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.Un_Done, r.Name, target.DisplayName,
                    r.Method == InstallMethod.Yak ? Strings.Card_InstallMethodYak : Strings.Un_MethodBuiltIn));
            }
            catch (Exception e)
            {
                ok = false;
                lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.Un_Failed, $"{p.Name}: {e.Message}"));
            }
        }

        lines.AddRange(blocked.Select(b => string.Format(CultureInfo.CurrentCulture, Strings.Un_Failed, b)));
        return new UninstallOutcome(ok && blocked.Count == 0, string.Join(Environment.NewLine, lines));
    }

    private static UninstallOutcome Failed(string reason) =>
        new(false, string.Format(CultureInfo.CurrentCulture, Strings.Un_Failed, reason));

    internal static string ConfirmText(UninstallPreview preview, RhinoTarget target)
    {
        var lines = new List<string>
        {
            string.Format(CultureInfo.CurrentCulture, Strings.Un_Confirm,
                preview.Versions.Count == 1 ? $"{preview.Name} {preview.Versions[0]}" : preview.Name, target.DisplayName),
        };
        if (preview.Versions.Count > 1)
            lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.Un_AllVersions, string.Join(", ", preview.Versions)));
        if (preview.RhinoRunning)
            lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.Un_Running, target.DisplayName));
        return string.Join(Environment.NewLine, lines);
    }
}
