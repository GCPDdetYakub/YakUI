using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using YakUI.Core.Api;
using YakUI.Core.Compat;
using YakUI.Core.Storage;
using YakUI.Core.Localization;

namespace YakUI.Core.Catalog;

/// <summary>Данные каталога с признаком происхождения (FR-29: офлайн-режим с давностью данных).</summary>
public sealed record CatalogResult<T>(T Value, DateTimeOffset FetchedAt, bool IsOffline, string? OfflineReason = null);

public sealed class CatalogUnavailableException : Exception
{
    public CatalogUnavailableException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Каталог Yak с кэшем в SQLite (FR-20, FR-29, AD-06).
/// Индекс <c>/packages</c> свеж в течение TTL; версии пакета инвалидируются по <c>updated_at</c> из индекса.
/// Версии всех пакетов загружает фоновый <see cref="CatalogCrawler"/>; при недоступности сервера отдаётся кэш.
/// </summary>
public sealed class CatalogService
{
    private const string IndexFetchedAtKey = "index_fetched_at";

    private readonly YakApiClient _api;
    private readonly YakUiDatabase _db;
    private readonly TimeProvider _time;
    private readonly TimeSpan _indexTtl;

    public CatalogService(YakApiClient api, YakUiDatabase db, TimeSpan indexTtl, TimeProvider? time = null)
    {
        _api = api;
        _db = db;
        _indexTtl = indexTtl;
        _time = time ?? TimeProvider.System;
    }

    internal YakApiClient Api => _api;

    public async Task<CatalogResult<IReadOnlyList<PackageSummary>>> GetIndexAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        var fetchedAt = ReadIndexFetchedAt();
        if (!forceRefresh && fetchedAt is { } fresh && _time.GetUtcNow() - fresh < _indexTtl)
            return new(ReadIndex(), fresh, IsOffline: false);

        try
        {
            var packages = await _api.GetPackagesAsync(ct).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            WriteIndex(packages, now);
            return new(packages, now, IsOffline: false);
        }
        catch (YakApiException e)
        {
            if (fetchedAt is { } cached)
                return new(ReadIndex(), cached, IsOffline: true, e.Message);
            throw new CatalogUnavailableException(L.T("CatalogService.31", $"Сервер Yak недоступен, а кэш каталога пуст."), e);
        }
    }

    public async Task<CatalogResult<IReadOnlyList<PackageVersionInfo>>> GetVersionsAsync(string name, CancellationToken ct = default)
    {
        var indexUpdatedAt = ReadIndexUpdatedAt(name);
        var cached = ReadVersions(name);

        // Кэш годен, если пакет в индексе не менялся с момента загрузки версий.
        if (cached is { } c && indexUpdatedAt is not null && c.UpdatedAt == indexUpdatedAt)
            return new(c.Versions, c.FetchedAt, IsOffline: false);

        try
        {
            var versions = await _api.GetVersionsAsync(name, ct).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            WriteVersions(name, indexUpdatedAt, versions, now);
            return new(versions, now, IsOffline: false);
        }
        catch (YakApiException e)
        {
            if (cached is { } stale)
                return new(stale.Versions, stale.FetchedAt, IsOffline: true, e.Message);
            throw new CatalogUnavailableException(L.T("CatalogService.30", $"Сервер Yak недоступен, версии «{name}» не закэшированы."), e);
        }
    }

    /// <summary>
    /// Пакеты индекса, версии которых не закэшированы или устарели (<c>updated_at</c> изменился).
    /// Для них совместимость «уточняется» (FR-21); по этому же списку краулер продолжает работу после перезапуска.
    /// </summary>
    public IReadOnlyList<PackageSummary> GetPendingPackages() => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT i.json FROM catalog_index i
            LEFT JOIN catalog_versions v ON v.name = i.name
            WHERE v.name IS NULL OR v.updated_at IS NOT i.updated_at
            ORDER BY i.name COLLATE NOCASE;
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<PackageSummary>();
        while (r.Read())
        {
            if (JsonSerializer.Deserialize<PackageSummary>(r.GetString(0), YakApiClient.JsonOptions) is { } p)
                list.Add(p);
        }

        return (IReadOnlyList<PackageSummary>)list;
    });

    /// <summary>Удаляет из кэша версии пакетов, которых больше нет в индексе.</summary>
    public int RemoveVersionsMissingFromIndex() => _db.WithConnection(c =>
    {
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM catalog_distributions WHERE name NOT IN (SELECT name FROM catalog_index);
            DELETE FROM catalog_package_info WHERE name NOT IN (SELECT name FROM catalog_index);
            DELETE FROM catalog_versions WHERE name NOT IN (SELECT name FROM catalog_index);
            """;
        var removed = cmd.ExecuteNonQuery();
        tx.Commit();
        return removed;
    });

    /// <summary>
    /// Загружает версии одного пакета без внутренних повторов (темп задаёт краулер, ОВ-07) и сохраняет их
    /// вместе с <c>updated_at</c> из индекса.
    /// </summary>
    internal async Task FetchAndStoreAsync(PackageSummary package, CancellationToken ct)
    {
        var versions = await _api.GetVersionsAsync(package.Name, ct, retryTransient: false).ConfigureAwait(false);
        WriteVersions(package.Name, FormatTime(package.UpdatedAt), versions, _time.GetUtcNow());
    }

    /// <summary>
    /// Версии Rhino (major.minor), встреченные в тегах закэшированных дистрибутивов (FR-81b, FR-81c).
    /// Пока краулер не обошёл каталог, список неполный.
    /// </summary>
    public IReadOnlyList<RhinoRelease> GetKnownRhinoReleases() => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT rhino_version FROM catalog_distributions WHERE rhino_version IS NOT NULL;";
        using var r = cmd.ExecuteReader();
        var result = new HashSet<RhinoRelease>();
        while (r.Read())
        {
            var tag = DistributionTag.FromApi(r.GetString(0), null);
            if (tag.RhinoMajor is { } major)
                result.Add(new RhinoRelease(major, tag.RhinoMinor ?? 0));
        }

        return (IReadOnlyList<RhinoRelease>)result.Order().ToList();
    });

    /// <summary>Сводка кэша: пакетов в индексе, с загруженными версиями, дистрибутивов.</summary>
    public (int Packages, int WithVersions, int Distributions) GetCacheStats() => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT COUNT(*) FROM catalog_index),
                   (SELECT COUNT(*) FROM catalog_versions WHERE name IN (SELECT name FROM catalog_index)),
                   (SELECT COUNT(*) FROM catalog_distributions);
            """;
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
    });

    public DateTimeOffset? IndexFetchedAt => ReadIndexFetchedAt();

    /// <summary>
    /// Снимок каталога для фильтров и поиска (FR-22…FR-27): индекс, сведения о пакетах, все дистрибутивы.
    /// Три запроса без разбора JSON версий — укладывается в холодный старт NFR-02.
    /// </summary>
    public CatalogSnapshot LoadSnapshot() => _db.WithConnection(c =>
    {
        var info = new Dictionary<string, (string? Desc, string? Keywords, string? Authors, string? Home, string? Icon, DateTimeOffset? Created, long Month, string? FallbackIcon)>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name, description, keywords, authors, homepage_url, icon_url, created_at, downloads_last_month, fallback_icon_url FROM catalog_package_info;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                info[r.GetString(0)] = (
                    r.IsDBNull(1) ? null : r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? null : DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture),
                    r.GetInt64(7),
                    r.IsDBNull(8) ? null : r.GetString(8));
            }
        }

        // Дистрибутивы, сгруппированные по пакету и версии.
        var versions = new Dictionary<string, Dictionary<string, (bool Pre, DateTimeOffset? Created, List<DistributionTag> Tags, List<CatalogFile> Files)>>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name, version, prerelease, rhino_version, platform, version_created_at, url, filename FROM catalog_distributions;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = r.GetString(0);
                if (!versions.TryGetValue(name, out var byVersion))
                    versions[name] = byVersion = new Dictionary<string, (bool, DateTimeOffset?, List<DistributionTag>, List<CatalogFile>)>(StringComparer.Ordinal);
                var ver = r.GetString(1);
                if (!byVersion.TryGetValue(ver, out var entry))
                {
                    var created = r.IsDBNull(5) ? (DateTimeOffset?)null : DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture);
                    byVersion[ver] = entry = (r.GetInt64(2) != 0, created, [], []);
                }

                var tag = DistributionTag.FromApi(r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4));
                entry.Tags.Add(tag);
                entry.Files.Add(new CatalogFile(tag, r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)));
            }
        }

        var fresh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT i.name FROM catalog_index i JOIN catalog_versions v ON v.name = i.name WHERE v.updated_at IS i.updated_at;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                fresh.Add(r.GetString(0));
        }

        var packages = new List<CatalogPackage>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT json FROM catalog_index;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (JsonSerializer.Deserialize<PackageSummary>(r.GetString(0), YakApiClient.JsonOptions) is not { } s)
                    continue;

                info.TryGetValue(s.Name, out var i);
                var vs = versions.TryGetValue(s.Name, out var byVersion)
                    ? byVersion.Select(kv => new CatalogVersion(kv.Key, Versioning.PackageVersion.TryParse(kv.Key, out var pv) ? pv : null, kv.Value.Pre, kv.Value.Tags, kv.Value.Created, kv.Value.Files)).ToList()
                    : [];
                packages.Add(new CatalogPackage(s, i.Desc, i.Keywords, i.Authors, i.Home, i.Icon, i.Created, i.Month, vs, !fresh.Contains(s.Name), i.FallbackIcon));
            }
        }

        return new CatalogSnapshot(packages, ReadIndexFetchedAtUnlocked(c));
    });

    private static DateTimeOffset? ReadIndexFetchedAtUnlocked(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM catalog_meta WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", IndexFetchedAtKey);
        return cmd.ExecuteScalar() is string s ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Полная очистка кэша каталога (только данные сервера; журнал и настройки не трогаются).</summary>
    public void ClearCache() => _db.WithConnection(c =>
    {
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM catalog_distributions; DELETE FROM catalog_package_info; DELETE FROM catalog_versions; DELETE FROM catalog_index; DELETE FROM catalog_meta;";
        cmd.ExecuteNonQuery();
        tx.Commit();
    });

    private DateTimeOffset? ReadIndexFetchedAt() => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM catalog_meta WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", IndexFetchedAtKey);
        return cmd.ExecuteScalar() is string s ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture) : (DateTimeOffset?)null;
    });

    private IReadOnlyList<PackageSummary> ReadIndex() => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM catalog_index ORDER BY name COLLATE NOCASE;";
        using var r = cmd.ExecuteReader();
        var list = new List<PackageSummary>();
        while (r.Read())
        {
            if (JsonSerializer.Deserialize<PackageSummary>(r.GetString(0), YakApiClient.JsonOptions) is { } p)
                list.Add(p);
        }

        return (IReadOnlyList<PackageSummary>)list;
    });

    private string? ReadIndexUpdatedAt(string name) => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT updated_at FROM catalog_index WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() as string;
    });

    private void WriteIndex(IReadOnlyList<PackageSummary> packages, DateTimeOffset now) => _db.WithConnection(c =>
    {
        using var tx = c.BeginTransaction();

        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM catalog_index;";
            del.ExecuteNonQuery();
        }

        using (var ins = c.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR REPLACE INTO catalog_index (name, version, updated_at, json) VALUES ($n, $v, $u, $j);";
            var pn = ins.Parameters.Add("$n", SqliteType.Text);
            var pv = ins.Parameters.Add("$v", SqliteType.Text);
            var pu = ins.Parameters.Add("$u", SqliteType.Text);
            var pj = ins.Parameters.Add("$j", SqliteType.Text);
            foreach (var p in packages)
            {
                pn.Value = p.Name;
                pv.Value = p.Version ?? "";
                pu.Value = (object?)FormatTime(p.UpdatedAt) ?? DBNull.Value;
                pj.Value = JsonSerializer.Serialize(p, YakApiClient.JsonOptions);
                ins.ExecuteNonQuery();
            }
        }

        using (var meta = c.CreateCommand())
        {
            meta.Transaction = tx;
            meta.CommandText = "INSERT OR REPLACE INTO catalog_meta (key, value) VALUES ($k, $v);";
            meta.Parameters.AddWithValue("$k", IndexFetchedAtKey);
            meta.Parameters.AddWithValue("$v", now.ToString("O", CultureInfo.InvariantCulture));
            meta.ExecuteNonQuery();
        }

        tx.Commit();
    });

    private CachedVersions? ReadVersions(string name) => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json, updated_at, fetched_at FROM catalog_versions WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", name);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return null;

        var versions = JsonSerializer.Deserialize<List<PackageVersionInfo>>(r.GetString(0), YakApiClient.JsonOptions) ?? [];
        return new CachedVersions(versions, r.IsDBNull(1) ? null : r.GetString(1), DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture));
    });

    /// <summary>Версии и плоские дистрибутивы пакета — одной транзакцией.</summary>
    private void WriteVersions(string name, string? updatedAt, IReadOnlyList<PackageVersionInfo> versions, DateTimeOffset now) => _db.WithConnection(c =>
    {
        using var tx = c.BeginTransaction();

        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO catalog_versions (name, updated_at, fetched_at, json) VALUES ($n, $u, $f, $j);
                DELETE FROM catalog_distributions WHERE name = $n;
                """;
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$u", (object?)updatedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$f", now.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(versions, YakApiClient.JsonOptions));
            cmd.ExecuteNonQuery();
        }

        using (var ins = c.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO catalog_distributions (name, version, prerelease, rhino_version, platform, filename, url, size, version_created_at)
                VALUES ($n, $v, $p, $rv, $pl, $f, $u, $s, $vc);
                """;
            var pn = ins.Parameters.Add("$n", SqliteType.Text);
            var pv = ins.Parameters.Add("$v", SqliteType.Text);
            var pp = ins.Parameters.Add("$p", SqliteType.Integer);
            var prv = ins.Parameters.Add("$rv", SqliteType.Text);
            var ppl = ins.Parameters.Add("$pl", SqliteType.Text);
            var pf = ins.Parameters.Add("$f", SqliteType.Text);
            var pu = ins.Parameters.Add("$u", SqliteType.Text);
            var ps = ins.Parameters.Add("$s", SqliteType.Integer);
            var pvc = ins.Parameters.Add("$vc", SqliteType.Text);
            foreach (var v in versions)
            {
                foreach (var d in v.Distributions)
                {
                    pn.Value = name;
                    pv.Value = v.Version;
                    pp.Value = v.Prerelease ? 1 : 0;
                    prv.Value = (object?)d.RhinoVersion ?? DBNull.Value;
                    ppl.Value = (object?)d.Platform ?? DBNull.Value;
                    pf.Value = (object?)d.Filename ?? DBNull.Value;
                    pu.Value = (object?)d.Url ?? DBNull.Value;
                    ps.Value = (object?)d.Size ?? DBNull.Value;
                    pvc.Value = (object?)FormatTime(v.CreatedAt ?? d.CreatedAt) ?? DBNull.Value;
                    ins.ExecuteNonQuery();
                }
            }
        }

        // Сведения о пакете — из самой новой версии; дата создания — самая ранняя; загрузки за месяц — сумма по версиям.
        var latest = versions
            .OrderByDescending(v => Versioning.PackageVersion.TryParse(v.Version, out var pv) ? pv : null, Comparer<Versioning.PackageVersion?>.Create(
                (a, b) => a is null ? (b is null ? 0 : -1) : a.CompareTo(b)))
            .FirstOrDefault();
        using (var info = c.CreateCommand())
        {
            info.Transaction = tx;
            info.CommandText = """
                INSERT OR REPLACE INTO catalog_package_info (name, description, keywords, authors, homepage_url, icon_url, created_at, downloads_last_month, fallback_icon_url)
                VALUES ($n, $d, $k, $a, $h, $i, $c, $m, $fi);
                """;
            info.Parameters.AddWithValue("$n", name);
            info.Parameters.AddWithValue("$d", (object?)latest?.Description ?? DBNull.Value);
            info.Parameters.AddWithValue("$k", latest is null ? DBNull.Value : string.Join(' ', latest.Keywords));
            info.Parameters.AddWithValue("$a", (object?)latest?.Authors ?? DBNull.Value);
            info.Parameters.AddWithValue("$h", (object?)latest?.HomepageUrl ?? DBNull.Value);
            info.Parameters.AddWithValue("$i", (object?)latest?.IconUrl ?? DBNull.Value);
            info.Parameters.AddWithValue("$c", (object?)FormatTime(versions.Where(v => v.CreatedAt is not null).Select(v => v.CreatedAt).Min()) ?? DBNull.Value);
            info.Parameters.AddWithValue("$m", versions.Sum(v => v.Downloads?.LastMonth ?? 0));
            var fallbackIcon = versions
                .Where(v => !string.IsNullOrWhiteSpace(v.IconUrl))
                .OrderByDescending(v => Versioning.PackageVersion.TryParse(v.Version, out var pv) ? pv : null, Comparer<Versioning.PackageVersion?>.Create(
                    (a, b) => a is null ? (b is null ? 0 : -1) : a.CompareTo(b)))
                .FirstOrDefault()?.IconUrl;
            info.Parameters.AddWithValue("$fi", (object?)fallbackIcon ?? DBNull.Value);
            info.ExecuteNonQuery();
        }

        tx.Commit();
    });

    private static string? FormatTime(DateTimeOffset? t) => t?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed record CachedVersions(IReadOnlyList<PackageVersionInfo> Versions, string? UpdatedAt, DateTimeOffset FetchedAt);
}
