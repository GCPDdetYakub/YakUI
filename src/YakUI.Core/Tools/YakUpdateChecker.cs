using System.Globalization;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using YakUI.Core.Logging;
using YakUI.Core.Versioning;
using YakUI.Core.Localization;

namespace YakUI.Core.Tools;

public enum YakFoundBy
{
    Documentation,
    Probe,
}

/// <summary>Сборка yak на files.mcneel.com для текущего rid.</summary>
public sealed record YakAvailable(PackageVersion Version, Uri Url, YakFoundBy FoundBy, bool NewerThanDocs);

/// <summary>По версии Rhino: что используется и что можно скачать (FR-12).</summary>
public sealed record YakLineStatus(int RhinoMajor, YakTool? Current, YakAvailable? Newer)
{
    public bool HasUpdate => Newer is not null;
}

public sealed partial record YakCheckResult(IReadOnlyList<YakAvailable> Available, IReadOnlyList<YakLineStatus> Lines, DateTimeOffset CheckedAt, string? Problem);

/// <summary>
/// Поиск новых сборок yak (6.2, FR-12): шаг 1 — ссылки <c>files.mcneel.com/yak/tools/&lt;ver&gt;/&lt;rid&gt;/yak.exe</c> со страницы
/// документации; шаг 2 — HEAD-зондирование соседних версий от известных (patch+1…+3, minor+1…+2 с patch 0…3, major+1).
/// Шаг 0 (<c>latest/yak.exe</c>) не используется: там устаревший yak 0.12 (ОВ-12). Привязка новой сборки к версии Rhino
/// определяется только после скачивания (<c>yak list</c>); до этого кандидат относится к линейке той же «major.minor».
/// </summary>
public sealed partial class YakUpdateChecker : IDisposable
{
    public static readonly Uri DocsPage = new("https://developer.rhino3d.com/guides/yak/yak-cli-reference/");

    private readonly HttpClient _http;
    private readonly OperationJournal? _journal;
    private readonly TimeProvider _time;

    public YakUpdateChecker(HttpMessageHandler? handler = null, OperationJournal? journal = null, TimeProvider? time = null)
    {
        _http = handler is null ? new HttpClient(Net.SwitchableProxy.CreateHandler()) : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YakUI", AppInfo.Version));
        _journal = journal;
        _time = time ?? TimeProvider.System;
    }

    /// <param name="tools">Известные yak (встроенные, скачанные, ручные) — от них строятся кандидаты и линейки.</param>
    /// <param name="majors">Версии Rhino, для которых показывается статус.</param>
    public async Task<YakCheckResult> CheckAsync(IReadOnlyList<YakTool> tools, IEnumerable<int> majors, Func<int, YakTool?> selectFor, CancellationToken ct = default)
    {
        string? problem = null;
        var rid = YakDownloader.Rid;

        // Шаг 1: документация.
        var docs = new List<PackageVersion>();
        try
        {
            var html = await _http.GetStringAsync(DocsPage, ct).ConfigureAwait(false);
            docs = ParseDocsLinks(html, rid);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested)
                throw;
            problem = L.T("YakUpdateChecker.154", $"Страница документации недоступна: {e.Message}");
        }

        // Шаг 2: зондирование от всех известных версий.
        var known = tools.Select(t => t.Version).OfType<PackageVersion>().Concat(docs).ToList();
        var maxDocs = docs.Count == 0 ? null : docs.Max();
        var probed = new List<PackageVersion>();
        foreach (var candidate in Candidates(known).Where(c => !docs.Contains(c)))
        {
            if (await ExistsAsync(YakDownloader.UrlFor(candidate.Original, rid), ct).ConfigureAwait(false))
                probed.Add(candidate);
        }

        var available = docs.Select(v => new YakAvailable(v, YakDownloader.UrlFor(v.Original, rid), YakFoundBy.Documentation, false))
            .Concat(probed.Select(v => new YakAvailable(v, YakDownloader.UrlFor(v.Original, rid), YakFoundBy.Probe, maxDocs is null || v.CompareTo(maxDocs) > 0)))
            .OrderByDescending(a => a.Version)
            .ToList();

        // Линейка версии Rhino — «major.minor» используемого yak; новее — то, что в этой линейке выше используемого.
        var lines = majors.Distinct().OrderDescending().Select(m =>
        {
            var current = selectFor(m);
            var newer = current?.Version is { } cv
                ? available.Where(a => a.Version.Major == cv.Major && a.Version.Minor == cv.Minor && a.Version.CompareTo(cv) > 0).MaxBy(a => a.Version)
                : null;
            return new YakLineStatus(m, current, newer);
        }).ToList();

        var result = new YakCheckResult(available, lines, _time.GetUtcNow(), problem);
        _journal?.Write("yak-check", "поиск новых сборок yak", problem is null ? "ok" : "partial",
            details: new { rid, available = available.Select(a => $"{a.Version} ({a.FoundBy})"), problem });
        return result;
    }

    /// <summary>Ссылки на сборки текущего rid со страницы документации.</summary>
    public static List<PackageVersion> ParseDocsLinks(string html, string rid) =>
        DocsLink().Matches(html)
            .Where(m => m.Groups["rid"].Value.Equals(rid, StringComparison.OrdinalIgnoreCase))
            .Select(m => PackageVersion.TryParse(m.Groups["ver"].Value, out var v) ? v : null)
            .OfType<PackageVersion>()
            .Distinct()
            .ToList();

    /// <summary>Кандидаты зондирования (6.2 шаг 2): patch+1…+3, minor+1…+2 с patch 0…3, major+1.</summary>
    public static IReadOnlyList<PackageVersion> Candidates(IEnumerable<PackageVersion> known)
    {
        var set = new SortedSet<PackageVersion>();
        foreach (var v in known.Distinct())
        {
            var major = v.Major;
            var minor = v.Minor;
            var patch = v.Numbers.Count > 2 ? v.Numbers[2] : 0;
            for (var p = 1; p <= 3; p++)
                set.Add(V(major, minor, patch + p));
            for (var m = 1; m <= 2; m++)
            {
                for (var p = 0; p <= 3; p++)
                    set.Add(V(major, minor + m, p));
            }

            set.Add(V(major + 1, 0, 0));
        }

        foreach (var v in known)
            set.RemoveWhere(c => c.CompareTo(v) == 0);
        return [.. set];

        static PackageVersion V(int a, int b, int c) => PackageVersion.Parse(string.Create(CultureInfo.InvariantCulture, $"{a}.{b}.{c}"));
    }

    private async Task<bool> ExistsAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var r = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), ct).ConfigureAwait(false);
            return r.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested)
                throw;
            return false;
        }
    }

    public void Dispose() => _http.Dispose();

    [GeneratedRegex(@"files\.mcneel\.com/yak/tools/(?<ver>\d+\.\d+\.\d+)/(?<rid>[a-z0-9-]+)/yak\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex DocsLink();
}
