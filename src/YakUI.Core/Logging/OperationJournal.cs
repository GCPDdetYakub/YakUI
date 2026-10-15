using System.Globalization;
using System.Text.Json;
using YakUI.Core.Storage;

namespace YakUI.Core.Logging;

public sealed record JournalEntry(
    long Id,
    DateTimeOffset Timestamp,
    string UserName,
    string Kind,
    string Action,
    string Result,
    int? ExitCode,
    long? DurationMs,
    string? Details);

/// <summary>Журнал операций в SQLite (FR-90; вызовы yak — FR-16).</summary>
public sealed class OperationJournal
{
    public const string KindYak = "yak";

    private readonly YakUiDatabase _db;
    private readonly TimeProvider _time;
    private readonly string _userName;

    public OperationJournal(YakUiDatabase db, TimeProvider? time = null, string? userName = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
        _userName = userName ?? Environment.UserName;
    }

    public void Write(string kind, string action, string result, int? exitCode = null, TimeSpan? duration = null, object? details = null)
    {
        var detailsJson = details is null ? null : JsonSerializer.Serialize(details);
        _db.WithConnection(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO journal (timestamp, user_name, kind, action, result, exit_code, duration_ms, details)
                VALUES ($ts, $user, $kind, $action, $result, $exit, $dur, $details);
                """;
            cmd.Parameters.AddWithValue("$ts", _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$user", _userName);
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$action", action);
            cmd.Parameters.AddWithValue("$result", result);
            cmd.Parameters.AddWithValue("$exit", (object?)exitCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$dur", duration is null ? DBNull.Value : (long)duration.Value.TotalMilliseconds);
            cmd.Parameters.AddWithValue("$details", (object?)detailsJson ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        });
    }

    public IReadOnlyList<JournalEntry> ReadLatest(int count = 100) => _db.WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, timestamp, user_name, kind, action, result, exit_code, duration_ms, details FROM journal ORDER BY id DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", count);
        using var r = cmd.ExecuteReader();
        var list = new List<JournalEntry>();
        while (r.Read())
        {
            list.Add(new JournalEntry(
                r.GetInt64(0),
                DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture),
                r.GetString(2),
                r.GetString(3),
                r.GetString(4),
                r.GetString(5),
                r.IsDBNull(6) ? null : r.GetInt32(6),
                r.IsDBNull(7) ? null : r.GetInt64(7),
                r.IsDBNull(8) ? null : r.GetString(8)));
        }

        return (IReadOnlyList<JournalEntry>)list;
    });
}
