using Avalonia;
using YakUI.Platform.Windows;

namespace YakUI.App;

internal static class Program
{
    /// <summary>Право быть единственным окном YakUI (NFR-05a); null в тестах.</summary>
    internal static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // FR-89e: вспомогательный процесс с правами администратора — только восстановление hosts из копии YakUI.
        if (args.Length == 2 && args[0] == YakUI.Core.Hosts.HostsRestore.Argument)
            return RestoreHosts(args[1]);

        var paths = new WindowsPlatformPaths();

        // 16.1: portable-версия (portable.flag) в папке без права записи, например в Program Files.
        if (paths.IsPortable && !YakUI.Core.Platform.DirectoryAccess.CanWrite(paths.DataDirectory))
        {
            YakUI.Core.Localization.UiLanguage.Apply(YakUI.Core.Settings.AppLanguage.System);
            WindowsMessageBox.Warning(Resources.Strings.AppTitle, string.Format(System.Globalization.CultureInfo.CurrentCulture,
                Resources.Strings.Portable_NotWritable, AppContext.BaseDirectory));
            return 1;
        }
        using var instance = SingleInstance.TryAcquire(paths.DataDirectory);
        if (instance is null)
        {
            // Окно уже открыто — показать его и выйти.
            SingleInstance.SignalExisting(paths.DataDirectory);
            return 0;
        }

        Instance = instance;

        // Язык — до создания окон (FR-82); испорченные настройки не мешают запуску.
        try
        {
            YakUI.Core.Localization.UiLanguage.Apply(new YakUI.Core.Settings.SettingsStore(paths.ConfigDirectory).Load().Language);
        }
        catch (Exception)
        {
            YakUI.Core.Localization.UiLanguage.Apply(YakUI.Core.Settings.AppLanguage.System);
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Источник — только копия YakUI (<c>hosts.yakui.bak</c>), цель — системный hosts, вычисленный здесь же.</summary>
    private static int RestoreHosts(string backupPath)
    {
        try
        {
            if (!string.Equals(Path.GetFileName(backupPath), "hosts.yakui.bak", StringComparison.OrdinalIgnoreCase) || !File.Exists(backupPath))
                return 2;
            YakUI.Core.Hosts.HostsRestore.Apply(backupPath, WindowsHostsPlatform.SystemHostsPath);
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
