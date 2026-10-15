using YakUI.Core.Logging;

namespace YakUI.Core.Hosts;

/// <summary>
/// Резервная копия hosts на сеанс и отметка «сеанс изменения hosts открыт» (FR-89b, FR-89e, FR-89f).
/// Копия — <c>&lt;данные&gt;\backups\hosts\hosts.yakui.bak</c>, отметка — <c>session.open</c> рядом.
/// Содержимое hosts в журнал не пишется (FR-89d).
/// </summary>
public sealed class HostsSession(string hostsPath, string dataDirectory, OperationJournal? journal = null)
{
    public string HostsPath { get; } = hostsPath;

    public string BackupDirectory { get; } = Path.Combine(dataDirectory, "backups", "hosts");

    public string BackupPath => Path.Combine(BackupDirectory, "hosts.yakui.bak");

    private string MarkerPath => Path.Combine(BackupDirectory, "session.open");

    /// <summary>Копия уже сделана в этом сеансе — повторные открытия её не перезаписывают.</summary>
    public bool BackedUpThisSession { get; private set; }

    /// <summary>Отметка сеанса есть (в том числе оставшаяся от аварийного завершения или отказа от UAC).</summary>
    public bool IsOpen => File.Exists(MarkerPath);

    /// <summary>Первое открытие в сеансе: побайтная копия hosts (перезаписывает копию прошлого сеанса) и отметка.</summary>
    public void EnsureBackup()
    {
        if (BackedUpThisSession)
            return;
        Directory.CreateDirectory(BackupDirectory);
        var temp = BackupPath + ".tmp";
        File.Copy(HostsPath, temp, overwrite: true);
        File.Move(temp, BackupPath, overwrite: true);
        File.WriteAllText(MarkerPath, DateTimeOffset.Now.ToString("O"));
        BackedUpThisSession = true;
        journal?.Write("hosts", "backup", "ok", details: new { backup = BackupPath, size = new FileInfo(BackupPath).Length });
    }

    /// <summary>Сеанс открыт, копия есть и hosts от неё отличается — нужно восстановление.</summary>
    public bool HasChanges()
    {
        if (!IsOpen || !File.Exists(BackupPath))
            return false;
        try
        {
            return !File.ReadAllBytes(HostsPath).AsSpan().SequenceEqual(File.ReadAllBytes(BackupPath));
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>Сеанс закрыт: hosts восстановлен или не менялся. Копия остаётся до следующего сеанса.</summary>
    public void Close(string result)
    {
        if (File.Exists(MarkerPath))
            File.Delete(MarkerPath);
        BackedUpThisSession = false;
        journal?.Write("hosts", "session-close", result);
    }
}
