using System.Globalization;
using YakUI.Core.Storage;

namespace YakUI.Core.Install;

/// <summary>Закреплённая версия пакета в цели (FR-58): «Обновить всё» её пропускает (FR-59).</summary>
public sealed record PinRecord(string TargetKey, string Name, string Version, DateTimeOffset PinnedAt);

/// <summary>Закрепления версий в SQLite (локальное состояние YakUI, AD-06).</summary>
public sealed class PinRegistry
{
    private readonly YakUiDatabase _db;
    private readonly TimeProvider _time;

    public PinRegistry(YakUiDatabase db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
    }

    public PinRecord Pin(string targetKey, string name, string version) => _db.WithConnection(c =>
    {
        var record = new PinRecord(targetKey, name, version, _time.GetUtcNow());
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pins (target_key, name, version, pinned_at) VALUES ($t, $n, $v, $at)
            ON CONFLICT(target_key, name) DO UPDATE SET version = $v, pinned_at = $at;
            """;
        cmd.Parameters.AddWithValue("$t", targetKey);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$v", version);
        cmd.Parameters.AddWithValue("$at", record.PinnedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
        return record;
    });

    public void Unpin(string targetKey, string name) => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM pins WHERE target_key = $t AND name = $n;";
        cmd.Parameters.AddWithValue("$t", targetKey);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.ExecuteNonQuery();
    });

    public PinRecord? Find(string targetKey, string name) => ForTarget(targetKey).GetValueOrDefault(name);

    /// <summary>Закрепления цели по имени пакета (без учёта регистра).</summary>
    public IReadOnlyDictionary<string, PinRecord> ForTarget(string targetKey) => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT target_key, name, version, pinned_at FROM pins WHERE target_key = $t;";
        cmd.Parameters.AddWithValue("$t", targetKey);
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, PinRecord>(StringComparer.OrdinalIgnoreCase);
        while (r.Read())
            map[r.GetString(1)] = new PinRecord(r.GetString(0), r.GetString(1), r.GetString(2), DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture));
        return (IReadOnlyDictionary<string, PinRecord>)map;
    });
}
