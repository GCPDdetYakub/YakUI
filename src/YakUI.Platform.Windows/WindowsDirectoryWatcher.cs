using YakUI.Core.Platform;

namespace YakUI.Platform.Windows;

/// <summary>
/// FileSystemWatcher для профиля пакетов (FR-55a). Временные папки YakUI (<c>.yakui-*</c>) не считаются изменением;
/// ошибка или переполнение буфера — сигнал полного пересканирования.
/// </summary>
public sealed class WindowsDirectoryWatcher : IDirectoryWatcher
{
    public IDisposable? Watch(string directory, Action<bool> changed)
    {
        if (!Directory.Exists(directory))
            return null;

        var w = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };

        void OnChange(object sender, FileSystemEventArgs e)
        {
            if (!IsOwnTemp(e.Name) || (e is RenamedEventArgs r && !IsOwnTemp(r.OldName)))
                changed(false);
        }

        w.Created += OnChange;
        w.Deleted += OnChange;
        w.Changed += OnChange;
        w.Renamed += OnChange;
        w.Error += (_, _) => changed(true);
        w.EnableRaisingEvents = true;
        return w;
    }

    private static bool IsOwnTemp(string? relative) =>
        relative is not null && relative.StartsWith(".yakui-", StringComparison.OrdinalIgnoreCase);
}
