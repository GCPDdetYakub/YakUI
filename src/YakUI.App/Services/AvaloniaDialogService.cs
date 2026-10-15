using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using YakUI.App.Resources;

namespace YakUI.App.Services;

/// <summary>Модальное окно подтверждения поверх главного окна (FR-85).</summary>
public sealed class AvaloniaDialogService : IDialogService
{
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var result = false;
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            MaxWidth = 520,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var ok = new Button { Content = confirmText, Classes = { "accent" }, MinWidth = 100, IsDefault = true };
        var cancel = new Button { Content = Strings.Dlg_Cancel, MinWidth = 100, IsCancel = true };
        ok.Click += (_, _) => { result = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };

        if (owner is not null)
            await dialog.ShowDialog(owner);
        return result;
    }
}
