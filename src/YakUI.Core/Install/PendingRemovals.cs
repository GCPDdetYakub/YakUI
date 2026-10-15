using System.Globalization;
using YakUI.Core.Logging;
using YakUI.Core.Platform;
using YakUI.Core.Storage;

namespace YakUI.Core.Install;

/// <summary>Отложенное удаление папки прежней версии (FR-49, ТЗ 0.3.5): Rhino цели был открыт при смене версии.</summary>
public sealed record PendingRemoval(string Path, string InstallPath, string Package, string Version, DateTimeOffset CreatedAt);

/// <summary>
/// Отложенные удаления (FR-49): папка прежней версии удаляется автоматически, когда Rhino цели закрыт, — при каждом
/// сканировании профиля. Если версия снова стала активной (откат) — запись снимается без удаления.
/// Файл <c>.yak</c> в папке загрузок не трогается: по нему откат работает без сети.
/// </summary>
public sealed class PendingRemovals
{
    private readonly YakUiDatabase _db;
    private readonly IRhinoProcessProbe _processes;
    private readonly OperationJournal? _journal;
    private readonly TimeProvider _time;

    public PendingRemovals(YakUiDatabase db, IRhinoProcessProbe processes, OperationJournal? journal = null, TimeProvider? time = null)
    {
        _db = db;
        _processes = processes;
        _journal = journal;
        _time = time ?? TimeProvider.System;
    }

    public void Add(string path, string installPath, string package, string version) => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO pending_removals (path, install_path, package, version, created_at)
            VALUES ($p, $i, $n, $v, $at);
            """;
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$i", installPath);
        cmd.Parameters.AddWithValue("$n", package);
        cmd.Parameters.AddWithValue("$v", version);
        cmd.Parameters.AddWithValue("$at", _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    });

    public IReadOnlyList<PendingRemoval> All() => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path, install_path, package, version, created_at FROM pending_removals;";
        using var r = cmd.ExecuteReader();
        var list = new List<PendingRemoval>();
        while (r.Read())
            list.Add(new PendingRemoval(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture)));
        return (IReadOnlyList<PendingRemoval>)list;
    });

    private void Remove(string path) => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM pending_removals WHERE path = $p;";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.ExecuteNonQuery();
    });

    /// <summary>Выполнить отложенные удаления для закрытых Rhino; возвращает удалённые папки.</summary>
    public IReadOnlyList<string> Process()
    {
        var done = new List<string>();
        foreach (var p in All())
        {
            if (_processes.FindRunning(p.InstallPath).Count > 0)
                continue; // Rhino ещё открыт — ждём

            var packageDir = Path.GetDirectoryName(p.Path)!;
            var manifest = Path.Combine(packageDir, "manifest.txt");
            var active = File.Exists(manifest) ? File.ReadAllText(manifest).Trim() : null;
            if (!Directory.Exists(p.Path) || (active is not null && BuiltInInstaller.SameVersion(active, p.Version)))
            {
                Remove(p.Path); // уже нет или снова активна (откат) — удалять нечего
                continue;
            }

            var temp = Path.Combine(Path.GetDirectoryName(packageDir)!, StandardUninstaller.DeletePrefix + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.Move(p.Path, temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue; // файлы ещё заняты — попробуем при следующем сканировании
            }

            BuiltInInstaller.DeleteQuietly(temp);
            Remove(p.Path);
            done.Add(p.Path);
            _journal?.Write("version-change", $"{p.Package} {p.Version}", "deferred-removed", details: new { p.Path, p.CreatedAt });
        }

        return done;
    }
}
