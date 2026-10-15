using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using YakUI.App.ViewModels;

namespace YakUI.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // FR-101: перетаскивание файлов в окно — импорт для выбранной версии Rhino.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        });
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (DataContext is not MainWindowViewModel vm || e.DataTransfer.TryGetFiles() is not { } items)
                return;
            // Файлы и папки пакета целиком (13a).
            var files = items.Select(i => i.TryGetLocalPath()).OfType<string>().Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (files.Count > 0)
                await vm.ImportFilesAsync(files);
        });
    }
}
