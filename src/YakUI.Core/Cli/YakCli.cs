using YakUI.Core.Logging;
using YakUI.Core.Platform;
using YakUI.Core.Localization;

namespace YakUI.Core.Cli;

public sealed class YakCliException : Exception
{
    public YakCliException(string message, ProcessResult? result = null) : base(message) => Result = result;

    public ProcessResult? Result { get; }
}

/// <summary>
/// Запуск yak.exe. Каждый вызов журналируется: аргументы, stdout/stderr, код возврата, длительность (FR-16).
/// Проверка привязки к цели (FR-19a) — в <see cref="Tools.YakToolLibrary"/>.
/// </summary>
public sealed class YakCli
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    private readonly IProcessRunner _runner;
    private readonly OperationJournal? _journal;

    public YakCli(IProcessRunner runner, OperationJournal? journal = null, Func<IReadOnlyDictionary<string, string?>>? environment = null)
    {
        _runner = runner;
        _journal = journal;
        _environment = environment ?? (() => Net.SwitchableProxy.Shared.Settings.YakEnvironment());
    }

    private readonly Func<IReadOnlyDictionary<string, string?>> _environment;

    public async Task<ProcessResult> RunAsync(string yakPath, IReadOnlyList<string> args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var action = $"{Path.GetFileName(yakPath)} {string.Join(' ', args)}".Trim();
        ProcessResult result;
        try
        {
            // FR-95a: прокси из «Настроек» передаётся yak через переменные окружения.
            var env = _environment();
            result = env.Count == 0
                ? await _runner.RunAsync(yakPath, args, timeout ?? DefaultTimeout, ct).ConfigureAwait(false)
                : await _runner.RunAsync(yakPath, args, timeout ?? DefaultTimeout, env, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            _journal?.Write(OperationJournal.KindYak, action, "start-failed", details: new { yakPath, args, error = e.Message });
            throw new YakCliException(L.T("YakCli.35", $"Не удалось запустить {yakPath}: {e.Message}"));
        }

        _journal?.Write(
            OperationJournal.KindYak,
            action,
            result.TimedOut ? "timeout" : result.ExitCode == 0 ? "ok" : "error",
            result.ExitCode,
            result.Duration,
            new { yakPath, args, stdout = result.StdOut, stderr = result.StdErr });

        return result;
    }

    public async Task<YakVersionInfo> GetVersionAsync(string yakPath, CancellationToken ct = default)
    {
        var r = await RunAsync(yakPath, ["version"], TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        EnsureSuccess(r, "version");
        return YakOutputParser.ParseVersion(r.StdOut)
            ?? throw new YakCliException(L.T("YakCli.34", $"Не удалось разобрать вывод «yak version»: {r.StdOut.Trim()}"), r);
    }

    /// <summary>
    /// <c>yak list</c>. Используется и для определения привязки (FR-13); для просмотра установленного
    /// вызывающий обязан сначала проверить привязку (FR-19a).
    /// </summary>
    public async Task<YakListResult> ListAsync(string yakPath, CancellationToken ct = default)
    {
        var r = await RunAsync(yakPath, ["list"], TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        EnsureSuccess(r, "list");
        return YakOutputParser.ParseList(r.StdOut);
    }

    /// <summary>
    /// <c>yak install "&lt;файл.yak&gt;"</c> (AD-03). Код возврата не проверяется здесь: отказ yak — повод перейти
    /// на встроенный установщик, решение принимает вызывающий. Привязку к цели проверяет вызывающий (FR-19a).
    /// </summary>
    public Task<ProcessResult> InstallFileAsync(string yakPath, string yakFile, CancellationToken ct = default) =>
        RunAsync(yakPath, ["install", yakFile], TimeSpan.FromMinutes(10), ct);

    /// <summary>
    /// <c>yak uninstall &lt;имя&gt;</c> (FR-48): удаляет пакет из профиля, к которому привязан yak. Код возврата и итог
    /// проверяет вызывающий (по <c>yak list</c> и профилю); привязку — тоже вызывающий (FR-19a).
    /// </summary>
    public Task<ProcessResult> UninstallAsync(string yakPath, string packageName, CancellationToken ct = default) =>
        RunAsync(yakPath, ["uninstall", packageName], TimeSpan.FromMinutes(5), ct);

    private static void EnsureSuccess(ProcessResult r, string command)
    {
        if (r.TimedOut)
            throw new YakCliException(L.T("YakCli.33", $"yak {command}: превышено время ожидания."), r);
        if (r.ExitCode != 0)
            throw new YakCliException(L.T("YakCli.32", $"yak {command}: код {r.ExitCode}. {(r.StdErr.Length > 0 ? r.StdErr : r.StdOut).Trim()}"), r);
    }
}
