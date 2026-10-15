using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using YakUI.App.Resources;
using YakUI.App.ViewModels;
using YakUI.Core.Platform;

namespace YakUI.App.Services;

/// <summary>
/// Собственный диалог «Открыть с помощью» (FR-88, FR-88a): найденные редакторы, «Обзор…» для любого .exe,
/// необязательные аргументы и «Запомнить выбор» (включено по умолчанию).
/// </summary>
public sealed class AvaloniaHostsDialogs : IHostsDialogs
{
    public async Task<EditorChoice?> ChooseEditorAsync(IReadOnlyList<TextEditor> editors)
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
            return null;

        var items = editors.ToList();
        var list = new ListBox
        {
            ItemsSource = items.Select(Line).ToList(),
            SelectedIndex = items.Count > 0 ? 0 : -1,
            MaxHeight = 280,
            MinWidth = 520,
        };
        var arguments = new TextBox { Watermark = Strings.Hosts_Arguments };
        var remember = new CheckBox { Content = Strings.Hosts_Remember, IsChecked = true };
        var browse = new Button { Content = Strings.Hosts_Browse };
        var ok = new Button { Content = Strings.Hosts_OpenButton, Classes = { "accent" }, MinWidth = 100, IsDefault = true };
        var cancel = new Button { Content = Strings.Dlg_Cancel, MinWidth = 100, IsCancel = true };

        EditorChoice? result = null;
        var dialog = new Window
        {
            Title = Strings.Hosts_ChooseTitle,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        browse.Click += async (_, _) =>
        {
            var path = await AppShell.PickFileAsync(Strings.Hosts_PickExe, Strings.Hosts_PickExeFilter, "*.exe");
            if (path is null)
                return;
            items.Add(new TextEditor(Path.GetFileNameWithoutExtension(path), path));
            list.ItemsSource = items.Select(Line).ToList();
            list.SelectedIndex = items.Count - 1;
        };
        ok.Click += (_, _) =>
        {
            if (list.SelectedIndex < 0)
                return;
            var editor = items[list.SelectedIndex];
            var args = string.IsNullOrWhiteSpace(arguments.Text) ? null : arguments.Text.Trim();
            result = new EditorChoice(editor with { Arguments = args }, remember.IsChecked == true);
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = Strings.Hosts_ChooseHint, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
                list,
                browse,
                arguments,
                remember,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };

        await dialog.ShowDialog(owner);
        return result;
    }

    private static string Line(TextEditor e) => $"{e.Name} — {e.Path}";
}
