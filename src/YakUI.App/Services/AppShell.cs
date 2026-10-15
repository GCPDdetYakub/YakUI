using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using YakUI.Core.Settings;

namespace YakUI.App.Services;

/// <summary>Обращения к оболочке Avalonia: тема (FR-83) и диалоги выбора папки/файла.</summary>
public static class AppShell
{
    /// <summary>«Как в системе» — ThemeVariant.Default: Avalonia следует теме Windows и меняет её на лету.</summary>
    public static void ApplyTheme(AppTheme theme)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }

    /// <summary>Мастер первого запуска (FR-18) — модальное окно поверх главного.</summary>
    public static async Task ShowWizardAsync(ViewModels.FirstRunViewModel vm)
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var window = new Window
        {
            Title = Resources.Strings.Wiz_Title,
            Width = 760,
            Height = 560,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Content = new Views.FirstRunView { DataContext = vm },
        };
        vm.CloseRequested += window.Close;
        _ = vm.LoadAsync();
        if (owner is not null)
            await window.ShowDialog(owner);
        else
            window.Show();
    }

    /// <summary>Окно установки (FR-40, FR-45); null — отмена.</summary>
    public static async Task<ViewModels.InstallChoice?> ShowInstallOptionsAsync(ViewModels.InstallOptionsViewModel vm)
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
            return null;
        var window = new Window
        {
            Title = Resources.Strings.Io_WindowTitle,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Views.InstallOptionsView { DataContext = vm },
        };
        vm.CloseRequested += window.Close;
        await window.ShowDialog(owner);
        return vm.Result;
    }

    private static TopLevel? Top =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public static async Task<string?> PickFolderAsync(string title)
    {
        if (Top?.StorageProvider is not { CanPickFolder: true } sp)
            return null;
        var result = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    public static async Task<string?> PickFileAsync(string title, string filterName, string pattern)
    {
        if (Top?.StorageProvider is not { CanOpen: true } sp)
            return null;
        var result = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = pattern.Split(';', StringSplitOptions.RemoveEmptyEntries) }],
        });
        return result.FirstOrDefault()?.TryGetLocalPath();
    }
}
