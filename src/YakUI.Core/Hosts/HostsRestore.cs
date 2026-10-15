namespace YakUI.Core.Hosts;

/// <summary>
/// Восстановление hosts из копии (FR-89e): побайтно и атомарно — временный файл рядом с hosts, затем замена.
/// Выполняется во вспомогательном процессе с правами администратора (<c>YakUI.exe --restore-hosts &lt;копия&gt;</c>);
/// путь к hosts вспомогательный процесс вычисляет сам и из аргументов не берёт.
/// </summary>
public static class HostsRestore
{
    public const string Argument = "--restore-hosts";

    public static void Apply(string backupPath, string hostsPath)
    {
        var bytes = File.ReadAllBytes(backupPath);
        var temp = Path.Combine(Path.GetDirectoryName(hostsPath)!, "hosts.yakui.tmp");
        File.WriteAllBytes(temp, bytes);
        if (File.Exists(hostsPath))
            File.Replace(temp, hostsPath, destinationBackupFileName: null, ignoreMetadataErrors: true); // права и атрибуты hosts сохраняются
        else
            File.Move(temp, hostsPath);
    }
}
