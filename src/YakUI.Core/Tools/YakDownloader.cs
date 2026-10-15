using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using YakUI.Core.Cli;
using YakUI.Core.Logging;
using YakUI.Core.Platform;
using YakUI.Core.Versioning;
using YakUI.Core.Localization;

namespace YakUI.Core.Tools;

public sealed class YakDownloadException : Exception
{
    public YakDownloadException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Итог скачивания: где лежит, версия, привязка.</summary>
public sealed record YakDownloadResult(string Path, PackageVersion Version, int? BoundRhinoMajor, bool Signed, bool AlreadyPresent);

/// <summary>
/// Скачивание standalone yak.exe (FR-10, FR-11, FR-14, FR-15, FR-17): только HTTPS с files.mcneel.com, rid по архитектуре;
/// временная папка рядом → SHA-256 → подпись (McNeel или без подписи; другая или недействительная — карантин) →
/// снятие Mark-of-the-Web → <c>yak version</c> (версия должна совпасть) и <c>yak list</c> (привязка) → перенос в
/// <c>yak\&lt;ver&gt;\yak.exe</c>. Хранится по 3 последних сборки для каждой версии Rhino плюс закреплённые и используемые.
/// Используемая сборка на лету не заменяется: новая ложится рядом (FR-19).
/// </summary>
public sealed class YakDownloader : IDisposable
{
    public const string OfficialHost = "files.mcneel.com";
    public const int KeepPerRhino = 3;

    private readonly IPlatformPaths _paths;
    private readonly ISignatureVerifier _signatures;
    private readonly IMarkOfTheWeb _motw;
    private readonly YakCli _cli;
    private readonly YakDownloadRegistry _registry;
    private readonly HttpClient _http;
    private readonly OperationJournal? _journal;
    private readonly IAppLog? _log;
    private readonly TimeProvider _time;

    public YakDownloader(IPlatformPaths paths, ISignatureVerifier signatures, IMarkOfTheWeb motw, YakCli cli, YakDownloadRegistry registry,
        HttpMessageHandler? handler = null, OperationJournal? journal = null, IAppLog? log = null, TimeProvider? time = null)
    {
        _paths = paths;
        _signatures = signatures;
        _motw = motw;
        _cli = cli;
        _registry = registry;
        _journal = journal;
        _log = log;
        _time = time ?? TimeProvider.System;
        _http = handler is null
            ? new HttpClient(Net.SwitchableProxy.CreateHandler())
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YakUI", AppInfo.Version));
    }

    /// <summary><c>win-x64</c> или <c>win-arm64</c> по архитектуре системы (FR-10).</summary>
    public static string Rid => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    public static Uri UrlFor(string version, string? rid = null) =>
        new(string.Create(CultureInfo.InvariantCulture, $"https://{OfficialHost}/yak/tools/{version}/{rid ?? Rid}/yak.exe"));

    public static bool IsOfficialUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && u.Host.Equals(OfficialHost, StringComparison.OrdinalIgnoreCase) && u.AbsolutePath.StartsWith("/yak/tools/", StringComparison.OrdinalIgnoreCase);

    private string Root => _paths.YakToolsDirectory();

    public string TargetPath(string version) => Path.Combine(Root, version, "yak.exe");

    public async Task<YakDownloadResult> DownloadAsync(string version, IProgress<(long Done, long? Total)>? progress = null, CancellationToken ct = default)
    {
        if (!PackageVersion.TryParse(version, out var expected) || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new YakDownloadException(L.T("YakDownloader.146", $"Неверный номер версии yak: «{version}»."));

        var final = TargetPath(version);
        if (File.Exists(final) && _registry.All().Any(r => r.Path.Equals(final, StringComparison.OrdinalIgnoreCase)))
        {
            var known = await VerifyAsync(final, expected, ct).ConfigureAwait(false);
            return known with { AlreadyPresent = true };
        }

        var url = UrlFor(version);
        Directory.CreateDirectory(Root);
        var tempDir = Path.Combine(Root, ".tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var temp = Path.Combine(tempDir, "yak.exe");
        try
        {
            // 1. Скачать полностью; размер сверяется с Content-Length (FR-14).
            using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    throw new YakDownloadException(string.Create(CultureInfo.InvariantCulture, $"{url}: HTTP {(int)response.StatusCode}."));
                if (response.RequestMessage?.RequestUri is { } final2 && !IsOfficialUrl(final2.AbsoluteUri))
                    throw new YakDownloadException(L.T("YakDownloader.145", $"Перенаправление на неофициальный адрес: {final2}."));
                var total = response.Content.Headers.ContentLength;
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    progress?.Report((done, total));
                }

                if (total is { } t && t != done)
                    throw new YakDownloadException(L.Ti("YakDownloader.144", $"Файл скачан не полностью: {done} из {t} байт."));
            }

            var sha = await Sha256Async(temp, ct).ConfigureAwait(false);

            // 2. Подпись (FR-17): McNeel — принят; без подписи — принят, т. к. скачан YakUI с files.mcneel.com по HTTPS;
            //    чужая или недействительная — карантин.
            var sig = _signatures.Verify(temp);
            var signed = sig.State == SignatureState.Valid && sig.IsMcNeel;
            if (sig.State == SignatureState.Invalid || (sig.State == SignatureState.Valid && !sig.IsMcNeel))
            {
                Quarantine(tempDir, version, L.T("YakDownloader.143", $"подпись не принята ({sig.State}, {sig.Publisher ?? L.T("YakDownloader.142", $"нет издателя")})"));
                throw new YakDownloadException(L.T("YakDownloader.141", $"yak {version}: подпись не принята (издатель: {sig.Publisher ?? L.T("YakDownloader.140", $"нет")}, состояние: {sig.State}); файл в карантине."));
            }

            // 3. Mark-of-the-Web снимается только после проверки подписи (FR-15).
            _motw.Remove(temp);

            // 4. Перенос рядом с используемыми (FR-19): старые сборки не трогаются.
            var dir = Path.GetDirectoryName(final)!;
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true); // сборка той же версии без записи в реестре — недокачанная или чужая
            Directory.Move(tempDir, dir);
            _registry.Add(new YakDownloadRecord(final, version, Rid, url.AbsoluteUri, sha, _time.GetUtcNow()));

            // 5. Запуск: версия совпадает, привязка определяется (FR-14, FR-13).
            YakDownloadResult result;
            try
            {
                result = await VerifyAsync(final, expected, ct).ConfigureAwait(false) with { Signed = signed };
            }
            catch (YakDownloadException e)
            {
                _registry.Remove(final);
                Quarantine(dir, version, e.Message);
                throw;
            }

            _journal?.Write("yak-download", $"yak {version} ({Rid})", "ok",
                details: new { url = url.AbsoluteUri, path = final, sha256 = sha, signed, publisher = sig.Publisher, binding = result.BoundRhinoMajor });
            _log?.Info($"Скачан yak {version}: {final}, {(signed ? "подписан McNeel" : "без подписи (files.mcneel.com, HTTPS)")}, привязка Rhino {result.BoundRhinoMajor?.ToString(CultureInfo.InvariantCulture) ?? "?"}.");
            return result;
        }
        catch (Exception e) when (e is not YakDownloadException and not OperationCanceledException)
        {
            _journal?.Write("yak-download", $"yak {version}", "error", details: new { url = url.AbsoluteUri, error = e.Message });
            throw new YakDownloadException(L.T("YakDownloader.139", $"Не удалось скачать yak {version}: {e.Message}"), e);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                TryDelete(tempDir);
        }
    }

    private async Task<YakDownloadResult> VerifyAsync(string path, PackageVersion expected, CancellationToken ct)
    {
        YakVersionInfo info;
        YakListResult list;
        try
        {
            info = await _cli.GetVersionAsync(path, ct).ConfigureAwait(false);
            list = await _cli.ListAsync(path, ct).ConfigureAwait(false);
        }
        catch (YakCliException e)
        {
            throw new YakDownloadException(L.T("YakDownloader.138", $"Скачанный yak не запускается: {e.Message}"));
        }

        if (!info.YakVersion.Numbers.Take(3).SequenceEqual(expected.Numbers.Take(3)))
            throw new YakDownloadException(L.T("YakDownloader.137", $"yak сообщает версию {info.YakVersion}, ожидалась {expected}."));
        if (list.BoundRhinoMajor is null)
            throw new YakDownloadException(L.T("YakDownloader.136", $"Не удалось определить привязку: «yak list» не сообщил каталог пакетов."));
        return new YakDownloadResult(path, info.YakVersion, list.BoundRhinoMajor, Signed: _signatures.Verify(path) is { State: SignatureState.Valid, IsMcNeel: true }, AlreadyPresent: false);
    }

    /// <summary>
    /// Хранение (FR-11, уточнение владельца): для каждой версии Rhino — 3 последних скачанных сборки, плюс закреплённые
    /// и используемые сейчас. Удаляются только скачанные YakUI сборки из <c>yak\</c>.
    /// </summary>
    public IReadOnlyList<string> Prune(IEnumerable<YakTool> tools, IEnumerable<string> keepPaths)
    {
        var keep = new HashSet<string>(keepPaths, StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(Root).TrimEnd('\\') + "\\";
        var removed = new List<string>();
        var downloaded = tools.Where(t => t.Origin == YakToolOrigin.Downloaded && Path.GetFullPath(t.Path).StartsWith(root, StringComparison.OrdinalIgnoreCase));
        foreach (var group in downloaded.GroupBy(t => t.BoundRhinoMajor))
        {
            foreach (var t in group.OrderByDescending(t => t.Version).Skip(KeepPerRhino).Where(t => !keep.Contains(t.Path)))
            {
                var dir = Path.GetDirectoryName(t.Path)!;
                if (TryDelete(dir))
                {
                    _registry.Remove(t.Path);
                    removed.Add(t.Path);
                }
            }
        }

        if (removed.Count > 0)
            _journal?.Write("yak-prune", $"удалено сборок: {removed.Count}", "ok", details: removed);
        return removed;
    }

    private void Quarantine(string dir, string version, string reason)
    {
        try
        {
            var q = Path.Combine(Root, ".quarantine", $"{version}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.GetDirectoryName(q)!);
            Directory.Move(dir, q);
            File.WriteAllText(Path.Combine(q, "reason.txt"), reason);
            _journal?.Write("yak-download", $"yak {version}", "quarantined", details: new { reason, path = q });
            _log?.Warning($"yak {version} помещён в карантин: {reason} ({q}).");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(dir);
        }
    }

    private static bool TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var s = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(s, ct).ConfigureAwait(false));
    }

    public void Dispose() => _http.Dispose();
}
