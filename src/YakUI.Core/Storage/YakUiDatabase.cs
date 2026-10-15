using System.Globalization;
using Microsoft.Data.Sqlite;
using YakUI.Core.Localization;

namespace YakUI.Core.Storage;

/// <summary>Параметры открытия базы (NFR-05, NFR-05a).</summary>
public sealed record DatabaseOpenOptions
{
    /// <summary>
    /// Разрешены ли монопольные операции: миграция схемы и откладывание повреждённой базы.
    /// <c>YakUI.Cli</c> при открытом окне YakUI передаёт false (NFR-05a).
    /// </summary>
    public bool AllowExclusive { get; init; } = true;

    /// <summary>Сколько ждать блокировку другого процесса.</summary>
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeProvider? Time { get; init; }

    /// <summary>Только для тестов: остановить миграцию на этой версии схемы.</summary>
    internal int? MigrateUpTo { get; init; }

    /// <summary>
    /// Журнал открытий базы (диагностика повреждений, 21.7): текстовый файл вне базы — переживает её порчу.
    /// null — не вести.
    /// </summary>
    public string? OpenLogPath { get; init; }

    /// <summary>Кто открывает: «App», «CLI <команда>».</summary>
    public string? Caller { get; init; }
}

/// <summary>Монопольная операция с базой невозможна: её держит другой процесс или запрещено параметрами (NFR-05a).</summary>
public sealed class DatabaseBusyException : Exception
{
    public DatabaseBusyException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Локальная база YakUI (AD-06): кэш каталога, журнал, кэш инспекций. Схема версионируется через <c>PRAGMA user_version</c>.
/// Одно соединение на экземпляр; доступ сериализуется блокировкой.
/// <para>
/// NFR-05a: миграции выполняются в одной транзакции <c>BEGIN EXCLUSIVE</c>, версия перечитывается уже под блокировкой
/// (другой процесс мог мигрировать раньше). Файлы базы никогда не удаляются; повреждённая база откладывается
/// (переименование), только если её файлы не держит ни один другой процесс.
/// </para>
/// </summary>
public sealed class YakUiDatabase : IDisposable
{
    public const int SchemaVersion = 11;

    private const int SqliteCorrupt = 11;
    private const int SqliteNotADb = 26;

    private readonly SqliteConnection _connection;
    private readonly object _lock = new();

    private YakUiDatabase(SqliteConnection connection, DatabaseOpenOptions options)
    {
        _connection = connection;
        try
        {
            _connection.Open();
            Execute(string.Create(CultureInfo.InvariantCulture, $"PRAGMA busy_timeout = {(int)options.BusyTimeout.TotalMilliseconds};"));
            Execute("PRAGMA journal_mode = WAL;");
            // Явно: каждая фиксация сбрасывается на диск (диагностика повреждений, 21.7).
            Execute("PRAGMA synchronous = FULL;");
            Migrate(options);
        }
        catch
        {
            // Освободить файл, чтобы не держать его дольше, чем нужно.
            _connection.Dispose();
            throw;
        }
    }

    /// <summary>Путь, куда была отложена повреждённая база при этом открытии; null — повреждений не было.</summary>
    public string? QuarantinedPath { get; private set; }

    /// <summary>Что показала проверка целостности, если база была отложена.</summary>
    public string? QuarantineReason { get; private set; }

    /// <summary>
    /// Открывает базу. Повреждённая (<c>PRAGMA quick_check</c> не «ok») откладывается в <c>*.corrupt-&lt;время&gt;</c>
    /// вместе с <c>-wal</c>/<c>-shm</c> и создаётся новая (NFR-05) — только если разрешены монопольные операции
    /// и файлы не открыты другим процессом; иначе <see cref="DatabaseBusyException"/>.
    /// </summary>
    public static YakUiDatabase Open(string path, DatabaseOpenOptions? options = null)
    {
        options ??= new DatabaseOpenOptions();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            // Microsoft.Data.Sqlite сам повторяет команды при SQLITE_BUSY до DefaultTimeout — согласуем с busy_timeout.
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(options.BusyTimeout.TotalSeconds)),
        }.ToString();

        var sidecars = Sidecars(path);
        var problem = File.Exists(path) ? QuickCheck(path, options) : null;
        if (problem is not null)
        {
            var q = Quarantine(problem);
            LogOpen(options, path, sidecars, "corrupt: " + problem, q);
            return Fresh(q, problem);
        }

        try
        {
            var db = new YakUiDatabase(new SqliteConnection(cs), options);
            LogOpen(options, path, sidecars, "ok", null);
            return db;
        }
        catch (SqliteException e) when (e.SqliteErrorCode is SqliteCorrupt or SqliteNotADb)
        {
            // Порча обнаружилась только при миграции.
            var q = Quarantine(e.Message);
            LogOpen(options, path, sidecars, "corrupt on migrate: " + e.Message, q);
            return Fresh(q, e.Message);
        }
        catch (Exception e)
        {
            LogOpen(options, path, sidecars, "error: " + e.Message, null);
            throw;
        }

        YakUiDatabase Fresh(string quarantinedPath, string reason)
        {
            var db = new YakUiDatabase(new SqliteConnection(cs), options);
            db.QuarantinedPath = quarantinedPath;
            db.QuarantineReason = reason;
            return db;
        }

        string Quarantine(string reason)
        {
            if (!options.AllowExclusive)
                throw new DatabaseBusyException(L.T("YakUiDatabase.134", $"База повреждена ({reason}), но открыто окно YakUI: восстановление возможно только при закрытом окне."));

            SqliteConnection.ClearAllPools();
            EnsureNotHeldByOtherProcess(path, reason);

            var stamp = (options.Time ?? TimeProvider.System).GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var target = $"{path}.corrupt-{stamp}";

            // Улики до переноса: integrity_check только на чтение (журнал WAL не переносится и не удаляется).
            WriteIntegrityReport(path, target + ".integrity.txt", reason, Sidecars(path));
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(path + suffix))
                    File.Move(path + suffix, target + suffix, overwrite: false);
            }

            return target;
        }
    }

    /// <summary>База в памяти (тесты).</summary>
    public static YakUiDatabase InMemory() => new(new SqliteConnection("Data Source=:memory:"), new DatabaseOpenOptions());

    public int UserVersion => WithConnection(ReadUserVersion);

    internal T WithConnection<T>(Func<SqliteConnection, T> action)
    {
        lock (_lock)
            return action(_connection);
    }

    internal void WithConnection(Action<SqliteConnection> action)
    {
        lock (_lock)
            action(_connection);
    }

    /// <summary>
    /// Проверка целостности подключением только на чтение (21.7): такое подключение не переносит журнал WAL в основной
    /// файл и не удаляет его при закрытии — если база повреждена, улики остаются как были.
    /// </summary>
    private static string? QuickCheck(string path, DatabaseOpenOptions options)
    {
        try
        {
            return QuickCheck(ReadOnly(path, options), options.BusyTimeout);
        }
        catch (SqliteException e) when (e.SqliteErrorCode is not (SqliteCorrupt or SqliteNotADb))
        {
            // Только на чтение база в режиме WAL может не открыться (нет -shm, нет прав) — прежняя проверка.
            // Подключение на запись при закрытии может перенести журнал и удалить -wal, поэтому — только когда
            // открыто одно это приложение (AllowExclusive) и файлы никто не держит (21.7); иначе проверка пропускается.
            if (!options.AllowExclusive)
                return null;
            try
            {
                EnsureNotHeldByOtherProcess(path, L.T("YakUiDatabase.133", $"проверка целостности"));
            }
            catch (DatabaseBusyException)
            {
                return null;
            }

            var rw = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = Math.Max(1, (int)Math.Ceiling(options.BusyTimeout.TotalSeconds)),
            }.ToString();
            return QuickCheck(rw, options.BusyTimeout);
        }
    }

    private static string? QuickCheck(string cs, TimeSpan busyTimeout)
    {
        try
        {
            using var probe = new SqliteConnection(cs);
            probe.Open();
            using (var timeout = probe.CreateCommand())
            {
                timeout.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA busy_timeout = {(int)busyTimeout.TotalMilliseconds};");
                timeout.ExecuteNonQuery();
            }

            // Отдельной командой: ExecuteScalar вернул бы значение первой инструкции, а не quick_check.
            using var cmd = probe.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            var result = cmd.ExecuteScalar() as string;
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase) ? null : result ?? L.T("YakUiDatabase.132", $"quick_check без ответа");
        }
        catch (SqliteException e) when (e.SqliteErrorCode is SqliteCorrupt or SqliteNotADb)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// Файлы нельзя переименовывать, пока их держит другой процесс: SQLite на Windows открывает их с FILE_SHARE_DELETE,
    /// и переименование «удалось бы», а тот процесс продолжил бы писать в отложенный файл (NFR-05a).
    /// </summary>
    private static void EnsureNotHeldByOtherProcess(string path, string reason)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = path + suffix;
            if (!File.Exists(file))
                continue;
            try
            {
                using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException e)
            {
                throw new DatabaseBusyException(L.T("YakUiDatabase.131", $"База повреждена ({reason}), но её файл открыт другим процессом ({Path.GetFileName(file)}). Закройте YakUI и повторите."), e);
            }
        }
    }

    private static string ReadOnly(string path, DatabaseOpenOptions options) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false,
        DefaultTimeout = Math.Max(1, (int)Math.Ceiling(options.BusyTimeout.TotalSeconds)),
    }.ToString();

    private static string Sidecars(string path)
    {
        static string One(string file) => File.Exists(file)
            ? string.Create(CultureInfo.InvariantCulture, $"{new FileInfo(file).Length} B, {File.GetLastWriteTime(file):yyyy-MM-dd HH:mm:ss}")
            : "нет";
        return $"db: {One(path)}; wal: {One(path + "-wal")}; shm: {One(path + "-shm")}";
    }

    /// <summary><c>PRAGMA integrity_check</c> рядом с отложенной базой (21.7).</summary>
    private static void WriteIntegrityReport(string path, string reportPath, string reason, string sidecars)
    {
        var sb = new System.Text.StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:O}  PID {Environment.ProcessId}  {Environment.CommandLine}")
            .AppendLine(CultureInfo.InvariantCulture, $"quick_check: {reason}")
            .AppendLine(sidecars);
        try
        {
            using var c = new SqliteConnection(ReadOnly(path, new DatabaseOpenOptions()));
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check(200);";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                sb.AppendLine(r.GetString(0));
        }
        catch (Exception e) when (e is SqliteException or IOException)
        {
            sb.AppendLine("integrity_check: " + e.Message);
        }

        try
        {
            File.WriteAllText(reportPath, sb.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // отчёт — вспомогательный; база всё равно откладывается
        }
    }

    /// <summary>Строка журнала открытий: время, PID, кто и с какой командой, файлы базы до открытия, итог (21.7).</summary>
    private static void LogOpen(DatabaseOpenOptions options, string path, string sidecars, string result, string? quarantined)
    {
        if (options.OpenLogPath is not { } log)
            return;
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}\tPID {Environment.ProcessId}\t{options.Caller ?? "?"}\texclusive={options.AllowExclusive}\t{sidecars}\t{result}{(quarantined is null ? "" : "\t→ " + quarantined)}\t{path}{Environment.NewLine}");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                using var s = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                s.Write(bytes);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(20);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private static int ReadUserVersion(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Миграция под монопольной блокировкой (NFR-05a): <c>BEGIN EXCLUSIVE</c> ждёт другие процессы (busy_timeout),
    /// версия перечитывается внутри транзакции, все шаги и новая <c>user_version</c> фиксируются одним COMMIT.
    /// </summary>
    private void Migrate(DatabaseOpenOptions options)
    {
        var target = options.MigrateUpTo ?? SchemaVersion;
        var version = UserVersion;
        if (version > SchemaVersion)
            throw new InvalidOperationException(L.T("YakUiDatabase.130", $"База создана более новой версией YakUI (схема {version}, поддерживается {SchemaVersion})."));
        if (version >= target)
            return;
        if (!options.AllowExclusive)
            throw new DatabaseBusyException(L.T("YakUiDatabase.129", $"Нужно обновить базу (схема {version} → {target}), но открыто окно YakUI. Закройте окно и повторите, либо запустите YakUI — он обновит базу сам."));

        WithConnection(c =>
        {
            // Захват блокировки короткими попытками: при плотной записи другого процесса встроенный обработчик
            // занятости SQLite может «голодать». Общее ожидание ограничено BusyTimeout.
            var deadline = DateTime.UtcNow + options.BusyTimeout;
            Exec(c, "PRAGMA busy_timeout = 50;");
            try
            {
                while (true)
                {
                    try
                    {
                        Exec(c, "BEGIN EXCLUSIVE;");
                        break;
                    }
                    catch (SqliteException e) when (e.SqliteErrorCode == 5) // SQLITE_BUSY
                    {
                        if (DateTime.UtcNow >= deadline)
                            throw new DatabaseBusyException(L.T("YakUiDatabase.128", $"База занята другим процессом: не удалось получить монопольную блокировку для обновления схемы."), e);
                        Thread.Sleep(Random.Shared.Next(1, 15));
                    }
                }
            }
            finally
            {
                Exec(c, string.Create(CultureInfo.InvariantCulture, $"PRAGMA busy_timeout = {(int)options.BusyTimeout.TotalMilliseconds};"));
            }

            try
            {
                var current = ReadUserVersion(c);
                for (var step = current + 1; step <= target; step++)
                    Exec(c, Steps[step - 1] + string.Create(CultureInfo.InvariantCulture, $"\nPRAGMA user_version = {step};"));
                Exec(c, "COMMIT;");
            }
            catch
            {
                Exec(c, "ROLLBACK;");
                throw;
            }
        });

        static void Exec(SqliteConnection c, string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Шаги схемы; индекс = версия − 1. Только DDL и очистка кэш-таблиц — файлы не трогаются.</summary>
    private static readonly string[] Steps =
    [
        // v1: каталог, журнал.
        """
        CREATE TABLE catalog_meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        CREATE TABLE catalog_index (
            name       TEXT PRIMARY KEY COLLATE NOCASE,
            version    TEXT NOT NULL,
            updated_at TEXT,
            json       TEXT NOT NULL
        );
        CREATE TABLE catalog_versions (
            name       TEXT PRIMARY KEY COLLATE NOCASE,
            updated_at TEXT,
            fetched_at TEXT NOT NULL,
            json       TEXT NOT NULL
        );
        CREATE TABLE journal (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp   TEXT NOT NULL,
            user_name   TEXT NOT NULL,
            kind        TEXT NOT NULL,
            action      TEXT NOT NULL,
            result      TEXT NOT NULL,
            exit_code   INTEGER,
            duration_ms INTEGER,
            details     TEXT
        );
        CREATE INDEX ix_journal_timestamp ON journal(timestamp);
        """,

        // v2: плоская таблица дистрибутивов для фильтра совместимости (FR-22) и списка версий Rhino (FR-81b/c).
        // Кэш версий пересобирается краулером.
        """
        CREATE TABLE catalog_distributions (
            name          TEXT NOT NULL COLLATE NOCASE,
            version       TEXT NOT NULL,
            prerelease    INTEGER NOT NULL,
            rhino_version TEXT,
            platform      TEXT,
            filename      TEXT,
            url           TEXT,
            size          INTEGER
        );
        CREATE INDEX ix_catalog_distributions_name ON catalog_distributions(name);
        CREATE INDEX ix_catalog_distributions_rhino ON catalog_distributions(rhino_version);
        DELETE FROM catalog_versions;
        """,

        // v3: сведения о пакете для поиска и сортировок (FR-25, FR-26) без разбора JSON при запуске (NFR-02).
        """
        CREATE TABLE catalog_package_info (
            name                 TEXT PRIMARY KEY COLLATE NOCASE,
            description          TEXT,
            keywords             TEXT,
            authors              TEXT,
            homepage_url         TEXT,
            icon_url             TEXT,
            created_at           TEXT,
            downloads_last_month INTEGER NOT NULL DEFAULT 0
        );
        DELETE FROM catalog_distributions;
        DELETE FROM catalog_versions;
        """,

        // v4: дата публикации каждой версии (FR-26) и кэш инспекций .yak (FR-31, FR-36b).
        """
        ALTER TABLE catalog_distributions ADD COLUMN version_created_at TEXT;
        CREATE TABLE yak_inspections (
            url          TEXT PRIMARY KEY,
            filename     TEXT,
            inspected_at TEXT NOT NULL,
            json         TEXT NOT NULL
        );
        DELETE FROM catalog_distributions;
        DELETE FROM catalog_package_info;
        DELETE FROM catalog_versions;
        """,

        // v5: icon_url самой новой версии, у которой он есть (FR-34: у последней версии его может не быть).
        // Кэш версий пересобирается краулером.
        """
        ALTER TABLE catalog_package_info ADD COLUMN fallback_icon_url TEXT;
        DELETE FROM catalog_distributions;
        DELETE FROM catalog_package_info;
        DELETE FROM catalog_versions;
        """,

        // v6: реестр загрузок .yak (FR-37: SHA-256, повторно не загружать). Кэш каталога не трогается.
        """
        CREATE TABLE downloads (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            url          TEXT NOT NULL UNIQUE,
            filename     TEXT NOT NULL,
            path         TEXT NOT NULL,
            size         INTEGER NOT NULL,
            sha256       TEXT NOT NULL,
            package      TEXT,
            version      TEXT,
            tag          TEXT,
            completed_at TEXT NOT NULL
        );
        CREATE INDEX ix_downloads_package ON downloads(package);
        """,

        // v7: дата файла на сервере из Last-Modified (FR-37b).
        """
        ALTER TABLE downloads ADD COLUMN server_modified_at TEXT;
        """,

        // v8: дата публикации версии у загрузки (FR-37b, основная дата на экране «Загрузки») и реестр установок YakUI
        // (FR-46/FR-48: без пригодного yak удаляется только поставленное встроенным установщиком).
        """
        ALTER TABLE downloads ADD COLUMN published_at TEXT;
        CREATE TABLE installs (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            target_key   TEXT NOT NULL,
            name         TEXT NOT NULL COLLATE NOCASE,
            version      TEXT NOT NULL,
            path         TEXT NOT NULL,
            method       TEXT NOT NULL,
            sha256       TEXT,
            installed_at TEXT NOT NULL,
            UNIQUE (target_key, name, version)
        );
        """,

        // v9: закреплённые версии пакетов по целям (FR-58, FR-59) и скачанные YakUI сборки yak.exe
        // (FR-10, FR-11, FR-17: SHA-256 и адрес скачивания — основание доверия неподписанному).
        """
        CREATE TABLE pins (
            target_key TEXT NOT NULL,
            name       TEXT NOT NULL COLLATE NOCASE,
            version    TEXT NOT NULL,
            pinned_at  TEXT NOT NULL,
            PRIMARY KEY (target_key, name)
        );
        CREATE TABLE yak_downloads (
            path          TEXT PRIMARY KEY COLLATE NOCASE,
            version       TEXT NOT NULL,
            rid           TEXT NOT NULL,
            url           TEXT NOT NULL,
            sha256        TEXT NOT NULL,
            downloaded_at TEXT NOT NULL
        );
        """,

        // v10: отложенное удаление папки прежней версии, пока Rhino открыт (FR-49, ТЗ 0.3.5).
        """
        CREATE TABLE pending_removals (
            path         TEXT PRIMARY KEY COLLATE NOCASE,
            install_path TEXT NOT NULL,
            package      TEXT NOT NULL,
            version      TEXT NOT NULL,
            created_at   TEXT NOT NULL
        );
        """,

        // v11: пользовательский путь и импорт (FR-42…FR-46, FR-102): режим установки, корневая папка, созданные YakUI
        // ссылки (.ghlink, файлы в UserObjects), источник и исходный файл.
        """
        ALTER TABLE installs ADD COLUMN mode TEXT NOT NULL DEFAULT 'Standard';
        ALTER TABLE installs ADD COLUMN root TEXT;
        ALTER TABLE installs ADD COLUMN links TEXT;
        ALTER TABLE installs ADD COLUMN source TEXT NOT NULL DEFAULT 'Catalog';
        ALTER TABLE installs ADD COLUMN source_file TEXT;
        """,
    ];

    private void Execute(string sql) => WithConnection(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    });

    public void Dispose() => _connection.Dispose();
}
