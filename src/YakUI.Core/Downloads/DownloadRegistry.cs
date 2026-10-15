using System.Globalization;
using YakUI.Core.Storage;

namespace YakUI.Core.Downloads;

/// <summary>Скачанный <c>.yak</c> в библиотеке загрузок (FR-35, FR-37).</summary>
public sealed record DownloadRecord(
    long Id,
    string Url,
    string Filename,
    string Path,
    long Size,
    string Sha256,
    string? Package,
    string? Version,
    string? Tag,
    DateTimeOffset CompletedAt,
    DateTimeOffset? ServerModifiedAt = null,
    DateTimeOffset? PublishedAt = null)
{
    public bool FileExists => File.Exists(Path);
}

/// <summary>Реестр загрузок в SQLite (FR-37): URL, файл, размер, SHA-256, пакет и версия.</summary>
public sealed class DownloadRegistry
{
    private readonly YakUiDatabase _db;

    public DownloadRegistry(YakUiDatabase db) => _db = db;

    public DownloadRecord? FindByUrl(string url) => Query("WHERE url = $u", ("$u", url)).FirstOrDefault();

    public IReadOnlyList<DownloadRecord> All() => Query("ORDER BY completed_at DESC");

    public DownloadRecord Upsert(DownloadRecord r) => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO downloads (url, filename, path, size, sha256, package, version, tag, completed_at, server_modified_at, published_at)
            VALUES ($u, $f, $p, $s, $h, $pk, $v, $t, $c, $sm, $pa)
            ON CONFLICT(url) DO UPDATE SET filename = $f, path = $p, size = $s, sha256 = $h, package = $pk, version = $v, tag = $t, completed_at = $c, server_modified_at = $sm, published_at = $pa
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$u", r.Url);
        cmd.Parameters.AddWithValue("$f", r.Filename);
        cmd.Parameters.AddWithValue("$p", r.Path);
        cmd.Parameters.AddWithValue("$s", r.Size);
        cmd.Parameters.AddWithValue("$h", r.Sha256);
        cmd.Parameters.AddWithValue("$pk", (object?)r.Package ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$v", (object?)r.Version ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", (object?)r.Tag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c", r.CompletedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$sm", r.ServerModifiedAt is { } sm ? sm.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("$pa", r.PublishedAt is { } pa ? pa.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        return r with { Id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) };
    });

    public void Remove(long id) => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM downloads WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    });

    private IReadOnlyList<DownloadRecord> Query(string where, params (string Name, object Value)[] args) => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT id, url, filename, path, size, sha256, package, version, tag, completed_at, server_modified_at, published_at FROM downloads {where};";
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        using var r = cmd.ExecuteReader();
        var list = new List<DownloadRecord>();
        while (r.Read())
        {
            list.Add(new DownloadRecord(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
                DateTimeOffset.Parse(r.GetString(9), CultureInfo.InvariantCulture),
                r.IsDBNull(10) ? null : DateTimeOffset.Parse(r.GetString(10), CultureInfo.InvariantCulture),
                r.IsDBNull(11) ? null : DateTimeOffset.Parse(r.GetString(11), CultureInfo.InvariantCulture)));
        }

        return (IReadOnlyList<DownloadRecord>)list;
    });
}
