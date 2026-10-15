using System.Globalization;
using YakUI.Core.Cli;
using YakUI.Core.Platform;
using YakUI.Core.Settings;
using YakUI.Core.Targets;
using YakUI.Core.Localization;

namespace YakUI.Core.Tools;

/// <summary>
/// Библиотека yak по версиям Rhino (FR-13). Собирает встроенные, скачанные и указанные вручную yak.exe,
/// проверяет подлинность (FR-17) и среду .NET (FR-14a), определяет привязку по <c>yak list</c>
/// и выбирает для каждой версии Rhino самый новый пригодный yak с учётом закрепления.
/// Неподтверждённые и отклонённые файлы не запускаются.
/// Скачивание и проверка обновлений (FR-10, FR-12) — следующий этап.
/// </summary>
public sealed class YakToolLibrary
{
    private readonly IFileSystem _fs;
    private readonly IPlatformPaths _paths;
    private readonly ISignatureVerifier _signatures;
    private readonly RuntimeConfigChecker _runtime;
    private readonly YakCli _cli;
    private readonly AppSettings _settings;

    private List<YakTool> _tools = [];

    public YakToolLibrary(IFileSystem fs, IPlatformPaths paths, ISignatureVerifier signatures, IDotNetRuntimeProbe dotnet, YakCli cli, AppSettings settings)
    {
        _fs = fs;
        _paths = paths;
        _signatures = signatures;
        _runtime = new RuntimeConfigChecker(fs, dotnet);
        _cli = cli;
        _settings = settings;
    }

    public IReadOnlyList<YakTool> Tools => _tools;

    /// <summary>Неподписанный yak, скачанный YakUI (путь, SHA-256) — доверенный (FR-17); задаёт приложение.</summary>
    public Func<string, string, bool>? TrustedDownload { get; set; }

    /// <summary>Проверяет все известные yak.exe. Вызывает <c>yak version</c> и <c>yak list</c> у пригодных.</summary>
    public async Task<IReadOnlyList<YakTool>> RefreshAsync(IEnumerable<RhinoTarget> targets, CancellationToken ct = default)
    {
        var candidates = new List<(string Path, YakToolOrigin Origin)>();
        candidates.AddRange(targets.Where(t => t.BuiltInYakPath is not null).Select(t => (t.BuiltInYakPath!, YakToolOrigin.BuiltIn)));
        candidates.AddRange(_fs.EnumerateDirectories(_paths.YakToolsDirectory())
            .Select(d => System.IO.Path.Combine(d, "yak.exe"))
            .Where(_fs.FileExists)
            .Select(p => (p, YakToolOrigin.Downloaded)));
        candidates.AddRange((_settings.ManualYakPaths ?? []).Select(p => (p, YakToolOrigin.Manual)));

        var tools = new List<YakTool>();
        foreach (var (path, origin) in candidates.DistinctBy(c => System.IO.Path.GetFullPath(c.Path), StringComparer.OrdinalIgnoreCase))
            tools.Add(await InspectAsync(path, origin, ct).ConfigureAwait(false));

        _tools = tools;
        return tools;
    }

    internal async Task<YakTool> InspectAsync(string path, YakToolOrigin origin, CancellationToken ct)
    {
        if (!_fs.FileExists(path))
        {
            return new YakTool
            {
                Path = path,
                Origin = origin,
                Signature = new SignatureInfo(SignatureState.Unsigned, null),
                Trust = YakTrust.NeedsConfirmation,
                Runtime = RuntimeRequirement.SelfContained,
                Problem = L.T("YakToolLibrary.153", $"Файл не найден."),
            };
        }

        var signature = _signatures.Verify(path);
        string? sha = null;
        YakTrust trust;
        if (signature.State == SignatureState.Valid)
        {
            trust = signature.IsMcNeel ? YakTrust.SignedByMcNeel : YakTrust.Rejected;
        }
        else if (signature.State == SignatureState.Invalid)
        {
            trust = YakTrust.Rejected;
        }
        else
        {
            sha = _fs.ComputeSha256(path);
            trust = origin == YakToolOrigin.Downloaded && TrustedDownload?.Invoke(path, sha) == true
                ? YakTrust.UnsignedDownloaded
                : (_settings.ConfirmedUnsignedYakSha256 ?? []).Contains(sha, StringComparer.OrdinalIgnoreCase)
                    ? YakTrust.UnsignedAccepted
                    : YakTrust.NeedsConfirmation;
        }

        var runtime = _runtime.Check(path);
        var tool = new YakTool { Path = path, Origin = origin, Signature = signature, Trust = trust, Sha256 = sha, Runtime = runtime };

        switch (trust)
        {
            case YakTrust.Rejected:
                return tool with { Problem = L.T("YakToolLibrary.152", $"Подпись не принята (издатель: {signature.Publisher ?? L.T("YakToolLibrary.151", $"нет")}, состояние: {signature.State}).") };
            case YakTrust.NeedsConfirmation:
                return tool with { Problem = L.T("YakToolLibrary.150", $"Файл без подписи: нужно подтверждение пользователя (FR-17).") };
        }

        if (runtime.Problem is { } runtimeProblem)
            return tool with { Problem = runtimeProblem };

        try
        {
            var version = await _cli.GetVersionAsync(path, ct).ConfigureAwait(false);
            var list = await _cli.ListAsync(path, ct).ConfigureAwait(false);
            tool = tool with { VersionInfo = version, PackageDirectorySuffix = list.PackageDirectorySuffix, BoundRhinoMajor = list.BoundRhinoMajor };
            return list.BoundRhinoMajor is null
                ? tool with { Problem = L.T("YakToolLibrary.149", $"Не удалось определить привязку: в выводе «yak list» нет строки «Package directory: …\\packages\\<N.0>».") }
                : tool;
        }
        catch (YakCliException e)
        {
            return tool with { Problem = e.Message };
        }
    }

    /// <summary>
    /// yak для версии Rhino: закреплённый (если пригоден и привязан к ней), иначе самый новый пригодный;
    /// при равных версиях — встроенный (6.1). null — подходящего нет (AD-10).
    /// </summary>
    public YakTool? SelectFor(int rhinoMajor)
    {
        var usable = _tools.Where(t => t.IsUsable && t.BoundRhinoMajor == rhinoMajor).ToList();

        if ((_settings.PinnedYak ?? []).TryGetValue(rhinoMajor, out var pinned)
            && usable.FirstOrDefault(t => string.Equals(t.Path, pinned, StringComparison.OrdinalIgnoreCase)) is { } pinnedTool)
            return pinnedTool;

        return usable
            .OrderByDescending(t => t.Version)
            .ThenBy(t => t.Origin)
            .FirstOrDefault();
    }

    /// <summary>FR-19a: yak, привязанный к Rhino M, не используется для Rhino N ≠ M.</summary>
    public static void EnsureBinding(YakTool tool, RhinoTarget target)
    {
        if (tool.BoundRhinoMajor != target.Major)
        {
            throw new YakBindingException(L.Ti("YakToolLibrary.148", $"{tool.Path} привязан к Rhino {tool.BoundRhinoMajor?.ToString(CultureInfo.InvariantCulture) ?? "?"}, а цель — Rhino {target.Major}: yak поставил бы пакеты в чужой профиль (FR-19a)."));
        }
    }

    /// <summary>
    /// <c>yak list</c> для цели: привязка проверяется до вызова и повторно по ответу (FR-19a).
    /// </summary>
    public async Task<YakListResult> ListInstalledAsync(YakTool tool, RhinoTarget target, CancellationToken ct = default)
    {
        EnsureBinding(tool, target);
        var result = await _cli.ListAsync(tool.Path, ct).ConfigureAwait(false);
        if (result.BoundRhinoMajor != target.Major)
        {
            throw new YakBindingException(L.Ti("YakToolLibrary.147", $"{tool.Path} сообщил каталог «{result.PackageDirectorySuffix ?? "?"}», ожидался packages\\{target.ProfileKey} (FR-19a)."));
        }

        return result;
    }
}
