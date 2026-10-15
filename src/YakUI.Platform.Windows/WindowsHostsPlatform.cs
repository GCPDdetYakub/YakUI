using System.ComponentModel;
using System.Diagnostics;
using YakUI.Core.Hosts;
using YakUI.Core.Platform;

namespace YakUI.Platform.Windows;

/// <summary>Файл hosts на Windows (14.2): поиск редакторов, запуск через UAC, восстановление вспомогательным процессом.</summary>
public sealed class WindowsHostsPlatform(IRegistryReader registry) : IHostsPlatform
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: отказ в окне UAC

    /// <summary>Путь вычисляется из системной папки, не зашит как <c>C:\</c> (FR-87).</summary>
    public static string SystemHostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public string HostsPath => SystemHostsPath;

    /// <summary>Известные редакторы: имя, exe для App Paths, типовые пути установки.</summary>
    private static readonly (string Name, string Exe, string[] Typical)[] Known =
    [
        ("Notepad++", "notepad++.exe", [@"%ProgramFiles%\Notepad++\notepad++.exe", @"%ProgramFiles(x86)%\Notepad++\notepad++.exe"]),
        ("Visual Studio Code", "Code.exe", [@"%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe", @"%ProgramFiles%\Microsoft VS Code\Code.exe"]),
        ("Sublime Text", "sublime_text.exe", [@"%ProgramFiles%\Sublime Text\sublime_text.exe", @"%ProgramFiles%\Sublime Text 3\sublime_text.exe"]),
        ("Notepad3", "Notepad3.exe", [@"%ProgramFiles%\Notepad3\Notepad3.exe", @"%ProgramFiles(x86)%\Notepad3\Notepad3.exe"]),
        ("Notepad2", "Notepad2.exe", [@"%ProgramFiles%\Notepad2\Notepad2.exe"]),
        ("EmEditor", "EmEditor.exe", [@"%ProgramFiles%\EmEditor\EmEditor.exe"]),
        ("UltraEdit", "uedit64.exe", [@"%ProgramFiles%\IDM Computer Solutions\UltraEdit\uedit64.exe"]),
        ("Kate", "kate.exe", [@"%ProgramFiles%\Kate\bin\kate.exe"]),
    ];

    private const string AppPaths = @"Software\Microsoft\Windows\CurrentVersion\App Paths\";

    public IReadOnlyList<TextEditor> FindEditors()
    {
        var found = new List<TextEditor>();
        void Add(string name, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (File.Exists(path) && !found.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)))
                found.Add(new TextEditor(name, path));
        }

        Add(YakUI.Core.Localization.L.T("Hosts.Notepad", $"Блокнот"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"));

        foreach (var (name, exe, typical) in Known)
        {
            foreach (var root in new[] { RegistryRoot.CurrentUser, RegistryRoot.LocalMachine })
                Add(name, registry.GetString(root, AppPaths + exe, "", RegistryViewKind.Registry64));
            foreach (var t in typical)
                Add(name, t);
        }

        // Ассоциация .txt: выбор пользователя, затем класс по умолчанию.
        var progId = registry.GetString(RegistryRoot.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.txt\UserChoice", "ProgId", RegistryViewKind.Registry64)
            ?? registry.GetString(RegistryRoot.LocalMachine, @"Software\Classes\.txt", "", RegistryViewKind.Registry64);
        if (!string.IsNullOrEmpty(progId))
        {
            foreach (var root in new[] { RegistryRoot.CurrentUser, RegistryRoot.LocalMachine })
            {
                var command = registry.GetString(root, $@"Software\Classes\{progId}\shell\open\command", "", RegistryViewKind.Registry64);
                if (ExeFromCommand(command) is { } exe)
                    Add(Describe(exe), exe);
            }
        }

        return found;
    }

    /// <summary>Путь exe из командной строки ассоциации: <c>"C:\…\app.exe" "%1"</c> или <c>C:\…\app.exe %1</c>.</summary>
    internal static string? ExeFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }

        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : null;
    }

    private static string Describe(string exe)
    {
        try
        {
            var d = FileVersionInfo.GetVersionInfo(exe).FileDescription;
            return string.IsNullOrWhiteSpace(d) ? Path.GetFileNameWithoutExtension(exe) : d.Trim();
        }
        catch (FileNotFoundException)
        {
            return Path.GetFileNameWithoutExtension(exe);
        }
    }

    public EditorLaunch OpenEditor(TextEditor editor, string file, bool elevated)
    {
        var args = string.IsNullOrWhiteSpace(editor.Arguments) ? $"\"{file}\"" : $"{editor.Arguments} \"{file}\"";
        var psi = new ProcessStartInfo(editor.Path, args) { UseShellExecute = true, Verb = elevated ? "runas" : "" };
        try
        {
            var process = Process.Start(psi);
            // Редактор, передавший файл уже открытому окну, завершается сразу — индикатор обновится по наблюдению за файлом.
            return new EditorLaunch(ElevationResult.Ok, process is null ? Task.CompletedTask : WaitAsync(process));
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return new EditorLaunch(ElevationResult.Declined, Task.CompletedTask);
        }
        catch (Win32Exception e)
        {
            return new EditorLaunch(ElevationResult.Failed, Task.CompletedTask, e.Message);
        }
    }

    private static async Task WaitAsync(Process process)
    {
        using (process)
            await process.WaitForExitAsync().ConfigureAwait(false);
    }

    public async Task<ElevationResult> RestoreElevatedAsync(string backupPath)
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
            return ElevationResult.Failed;
        var psi = new ProcessStartInfo(exe, $"{HostsRestore.Argument} \"{backupPath}\"") { UseShellExecute = true, Verb = "runas" };
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return ElevationResult.Failed;
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode == 0 ? ElevationResult.Ok : ElevationResult.Failed;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return ElevationResult.Declined;
        }
        catch (Win32Exception)
        {
            return ElevationResult.Failed;
        }
    }
}
