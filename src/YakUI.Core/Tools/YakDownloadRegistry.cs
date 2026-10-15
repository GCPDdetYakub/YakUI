using System.Globalization;
using YakUI.Core.Storage;

namespace YakUI.Core.Tools;

/// <summary>Сборка yak.exe, скачанная YakUI с files.mcneel.com по HTTPS (FR-10, FR-17: основание доверия неподписанному).</summary>
public sealed record YakDownloadRecord(string Path, string Version, string Rid, string Url, string Sha256, DateTimeOffset DownloadedAt);

/// <summary>Реестр скачанных YakUI сборок yak в SQLite.</summary>
public sealed class YakDownloadRegistry
{
    private readonly YakUiDatabase _db;

    public YakDownloadRegistry(YakUiDatabase db) => _db = db;

    public void Add(YakDownloadRecord r) => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO yak_downloads (path, version, rid, url, sha256, downloaded_at)
            VALUES ($p, $v, $r, $u, $h, $at);
            """;
        cmd.Parameters.AddWithValue("$p", r.Path);
        cmd.Parameters.AddWithValue("$v", r.Version);
        cmd.Parameters.AddWithValue("$r", r.Rid);
        cmd.Parameters.AddWithValue("$u", r.Url);
        cmd.Parameters.AddWithValue("$h", r.Sha256);
        cmd.Parameters.AddWithValue("$at", r.DownloadedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    });

    public void Remove(string path) => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM yak_downloads WHERE path = $p;";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.ExecuteNonQuery();
    });

    public IReadOnlyList<YakDownloadRecord> All() => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path, version, rid, url, sha256, downloaded_at FROM yak_downloads;";
        using var r = cmd.ExecuteReader();
        var list = new List<YakDownloadRecord>();
        while (r.Read())
            list.Add(new YakDownloadRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture)));
        return (IReadOnlyList<YakDownloadRecord>)list;
    });

    /// <summary>
    /// Неподписанный yak принимается, только если его скачал YakUI с files.mcneel.com по HTTPS и SHA-256 совпадает (FR-17).
    /// </summary>
    public bool IsTrusted(string path, string sha256) =>
        All().Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(r.Sha256, sha256, StringComparison.OrdinalIgnoreCase)
                       && YakDownloader.IsOfficialUrl(r.Url));
}
