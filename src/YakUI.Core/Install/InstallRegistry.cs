using System.Globalization;
using YakUI.Core.Storage;

namespace YakUI.Core.Install;

public enum InstallMethod
{
    /// <summary><c>yak install "&lt;файл.yak&gt;"</c> yak-ом цели (AD-03).</summary>
    Yak,

    /// <summary>Встроенный установщик YakUI (FR-50).</summary>
    BuiltIn,
}

/// <summary>Режим установки (7.1).</summary>
public enum InstallMode
{
    /// <summary>Профиль пакетов цели (AD-03).</summary>
    Standard,

    /// <summary>Пользовательская папка: <c>&lt;Root&gt;/&lt;Name&gt;/&lt;Version&gt;/</c> + регистрация (FR-42…FR-44).</summary>
    Custom,

    /// <summary>«Только распаковать», без регистрации (FR-45).</summary>
    Unpack,
}

/// <summary>Откуда пакет (FR-102).</summary>
public enum InstallSource
{
    Catalog,

    /// <summary>Импорт файла (FR-101).</summary>
    File,

    /// <summary>Импорт файла, скачанного с Food4Rhino (по Mark-of-the-Web).</summary>
    Food4Rhino,
}

/// <summary>Запись реестра установок YakUI (FR-46): что и куда поставил YakUI.</summary>
public sealed record InstallRecord(
    long Id,
    string TargetKey,
    string Name,
    string Version,
    string Path,
    InstallMethod Method,
    string? Sha256,
    DateTimeOffset InstalledAt)
{
    public InstallMode Mode { get; init; } = InstallMode.Standard;

    /// <summary>Корневая папка пользовательского пути.</summary>
    public string? Root { get; init; }

    /// <summary>Созданные YakUI файлы вне папки версии: <c>.ghlink</c>, объекты в UserObjects (FR-43, FR-46).</summary>
    public IReadOnlyList<string> Links { get; init; } = [];

    public InstallSource Source { get; init; } = InstallSource.Catalog;

    /// <summary>Исходный файл импорта (FR-102).</summary>
    public string? SourceFile { get; init; }
}

/// <summary>
/// Реестр установок YakUI в SQLite. Нужен удалению без yak (FR-48): удалять можно только то,
/// что YakUI поставил сам встроенным установщиком.
/// </summary>
public sealed class InstallRegistry
{
    private readonly YakUiDatabase _db;

    public InstallRegistry(YakUiDatabase db) => _db = db;

    public InstallRecord Add(InstallRecord r) => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO installs (target_key, name, version, path, method, sha256, installed_at, mode, root, links, source, source_file)
            VALUES ($t, $n, $v, $p, $m, $h, $at, $mode, $root, $links, $src, $sf)
            ON CONFLICT(target_key, name, version) DO UPDATE SET path = $p, method = $m, sha256 = $h, installed_at = $at,
                mode = $mode, root = $root, links = $links, source = $src, source_file = $sf
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$t", r.TargetKey);
        cmd.Parameters.AddWithValue("$n", r.Name);
        cmd.Parameters.AddWithValue("$v", r.Version);
        cmd.Parameters.AddWithValue("$p", r.Path);
        cmd.Parameters.AddWithValue("$m", r.Method.ToString());
        cmd.Parameters.AddWithValue("$h", (object?)r.Sha256 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", r.InstalledAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$mode", r.Mode.ToString());
        cmd.Parameters.AddWithValue("$root", (object?)r.Root ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$links", r.Links.Count == 0 ? DBNull.Value : System.Text.Json.JsonSerializer.Serialize(r.Links));
        cmd.Parameters.AddWithValue("$src", r.Source.ToString());
        cmd.Parameters.AddWithValue("$sf", (object?)r.SourceFile ?? DBNull.Value);
        return r with { Id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) };
    });

    public InstallRecord? Find(string targetKey, string name, string version) => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, target_key, name, version, path, method, sha256, installed_at, mode, root, links, source, source_file FROM installs WHERE target_key = $t AND name = $n AND version = $v;";
        cmd.Parameters.AddWithValue("$t", targetKey);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$v", version);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    });

    public IReadOnlyList<InstallRecord> All() => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, target_key, name, version, path, method, sha256, installed_at, mode, root, links, source, source_file FROM installs ORDER BY id;";
        using var r = cmd.ExecuteReader();
        var list = new List<InstallRecord>();
        while (r.Read())
            list.Add(Read(r));
        return (IReadOnlyList<InstallRecord>)list;
    });

    /// <summary>Записи пакета в цели (все версии) в стандартном профиле.</summary>
    public IReadOnlyList<InstallRecord> ForPackage(string targetKey, string name) =>
        All().Where(r => r.Mode == InstallMode.Standard && r.TargetKey == targetKey && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Установки по пользовательскому пути и «только распаковать» (FR-46).</summary>
    public IReadOnlyList<InstallRecord> Custom() => All().Where(r => r.Mode != InstallMode.Standard).ToList();

    /// <summary>
    /// Сверка с диском (FR-55a): снимаются записи, папки версий которых больше нет, — кто бы ни удалил пакет.
    /// Возвращает снятые записи (для журнала).
    /// </summary>
    public IReadOnlyList<InstallRecord> RemoveStale(Func<string, bool> directoryExists)
    {
        var stale = All().Where(r => !directoryExists(r.Path)).ToList();
        foreach (var r in stale)
            Remove(r.Id);
        return stale;
    }

    private static InstallRecord Read(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
            Enum.Parse<InstallMethod>(r.GetString(5)), r.IsDBNull(6) ? null : r.GetString(6),
            DateTimeOffset.Parse(r.GetString(7), CultureInfo.InvariantCulture))
        {
            Mode = Enum.TryParse<InstallMode>(r.GetString(8), out var m) ? m : InstallMode.Standard,
            Root = r.IsDBNull(9) ? null : r.GetString(9),
            Links = r.IsDBNull(10) ? [] : System.Text.Json.JsonSerializer.Deserialize<List<string>>(r.GetString(10)) ?? [],
            Source = Enum.TryParse<InstallSource>(r.GetString(11), out var src) ? src : InstallSource.Catalog,
            SourceFile = r.IsDBNull(12) ? null : r.GetString(12),
        };

    public void Remove(long id) => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM installs WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    });
}
