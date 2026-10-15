namespace YakUI.Core.Platform;

// Платформенные интерфейсы (AD-08). Единственная реализация — проект YakUI.Platform.Windows.

public enum RegistryRoot
{
    LocalMachine,
    CurrentUser,
}

public enum RegistryViewKind
{
    Registry64,
    Registry32,
}

/// <summary>Чтение реестра. Если ключа нет или нет доступа — пустой результат, а не исключение.</summary>
public interface IRegistryReader
{
    IReadOnlyList<string> GetSubKeyNames(RegistryRoot root, string path, RegistryViewKind view);

    IReadOnlyList<string> GetValueNames(RegistryRoot root, string path, RegistryViewKind view);

    string? GetString(RegistryRoot root, string path, string name, RegistryViewKind view);
}

/// <summary>Файловая система, нужная ядру; подменяется в тестах.</summary>
/// <summary>Строки ресурса версии исполняемого файла.</summary>
public sealed record FileVersionStrings(string? FileDescription, string? SpecialBuild, string? ProductName);

public interface IFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    IEnumerable<string> EnumerateDirectories(string path);

    string ReadAllText(string path);

    /// <summary>FileVersion исполняемого файла или null.</summary>
    Version? GetFileVersion(string path);

    /// <summary>Строки сведений о версии (FR-81g); null — файла нет.</summary>
    FileVersionStrings? GetVersionStrings(string path);

    string ComputeSha256(string path);

    /// <summary>Все файлы папки рекурсивно; пустая последовательность, если папки нет.</summary>
    IEnumerable<string> EnumerateFilesRecursive(string path);

    /// <summary>Время создания папки или файла (UTC); null — нет.</summary>
    DateTimeOffset? GetCreationTime(string path);
}

/// <summary>Стандартные пути YakUI и Rhino (16.1, FR-02).</summary>
public interface IPlatformPaths
{
    /// <summary>Настройки: <c>%APPDATA%\YakUI</c> или <c>.\data\config</c> при <c>portable.flag</c>.</summary>
    string ConfigDirectory { get; }

    /// <summary>Кэш, yak, загрузки, база, логи: <c>%LOCALAPPDATA%\YakUI</c> или <c>.\data</c>.</summary>
    string DataDirectory { get; }

    bool IsPortable { get; }

    /// <summary><c>%APPDATA%\McNeel\Rhinoceros\packages</c>.</summary>
    string RhinoUserPackagesRoot { get; }

    /// <summary><c>%ProgramData%\McNeel\Rhinoceros</c> (профиль «для всех», только чтение).</summary>
    string RhinoProgramDataRoot { get; }

    /// <summary><c>%APPDATA%\Grasshopper\Libraries</c>.</summary>
    string GrasshopperLibraries { get; }

    /// <summary>Папки Program Files для запасного поиска Rhino и .NET.</summary>
    IReadOnlyList<string> ProgramFilesDirectories { get; }
}

public static class PlatformPathsExtensions
{
    public static string YakToolsDirectory(this IPlatformPaths p) => Path.Combine(p.DataDirectory, "yak");

    public static string DatabasePath(this IPlatformPaths p) => Path.Combine(p.DataDirectory, "yakui.db");
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, TimeSpan Duration, bool TimedOut);

/// <summary>Запуск внешних процессов (yak.exe).</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>С переменными окружения (FR-95a: прокси для yak); значение null — переменная удаляется.</summary>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken = default) =>
        RunAsync(fileName, arguments, timeout, cancellationToken);
}

public enum SignatureState
{
    /// <summary>Подпись Authenticode действительна.</summary>
    Valid,

    /// <summary>Файл не подписан.</summary>
    Unsigned,

    /// <summary>Подпись есть, но недействительна.</summary>
    Invalid,
}

public sealed record SignatureInfo(SignatureState State, string? Publisher)
{
    public const string McNeelPublisher = "Robert McNeel & Associates";

    /// <summary>
    /// Действительная подпись McNeel (FR-17). Издатель сравнивается без учёта регистра: встречаются
    /// <c>ROBERT MCNEEL &amp; ASSOCIATES (TLM, Inc.)</c> и <c>Robert McNeel &amp; Associates (TLM, Inc.)</c>.
    /// </summary>
    public bool IsMcNeel => State == SignatureState.Valid && IsMcNeelPublisher(Publisher);

    public static bool IsMcNeelPublisher(string? publisher) =>
        publisher is not null && publisher.Trim().StartsWith(McNeelPublisher, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Проверка подписи Authenticode (FR-17).</summary>
public interface ISignatureVerifier
{
    SignatureInfo Verify(string filePath);
}

/// <summary>Установленные общие среды .NET (FR-14a).</summary>
public interface IDotNetRuntimeProbe
{
    /// <summary>Версии фреймворка (<c>Microsoft.NETCore.App</c>, <c>Microsoft.WindowsDesktop.App</c>) для архитектуры системы.</summary>
    IReadOnlyList<Version> GetInstalledVersions(string frameworkName);
}

/// <summary>Открытие ссылки в системном браузере (FR-33). Только http/https.</summary>
public interface IUrlLauncher
{
    void Open(Uri url);
}

public static class UrlLauncherExtensions
{
    /// <summary>Открывает ссылку, если это корректный http(s)-адрес; иначе возвращает false.</summary>
    public static bool TryOpen(this IUrlLauncher launcher, string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return false;
        launcher.Open(uri);
        return true;
    }
}
/// <summary>Уменьшение картинок (FR-34c: иконки больше 256 px кладутся в кэш уменьшенными).</summary>
public interface IImageScaler
{
    /// <summary>Уменьшает PNG/JPEG так, чтобы большая сторона была не больше <paramref name="maxSide"/>; результат — PNG.</summary>
    byte[] DownscaleToPng(byte[] image, int maxSide);
}
/// <summary>Процесс, в котором работает Rhino цели: сам Rhino или хост с Rhino.Inside (FR-05).</summary>
public sealed record RunningRhino(int ProcessId, string ProcessName, DateTimeOffset StartTime, bool IsInsideHost);

/// <summary>Поиск запущенных Rhino для установки (FR-05).</summary>
public interface IRhinoProcessProbe
{
    /// <summary>Процессы, использующие Rhino из папки <paramref name="installPath"/>.</summary>
    IReadOnlyList<RunningRhino> FindRunning(string installPath);
}

/// <summary>Открыть папку в файловом менеджере (FR-58).</summary>
public interface IFolderOpener
{
    bool Open(string path);

    /// <summary>Открыть папку файла и выделить сам файл.</summary>
    bool Reveal(string filePath);
}
/// <summary>Метка Mark-of-the-Web (поток Zone.Identifier) у скачанного файла (FR-15).</summary>
public interface IMarkOfTheWeb
{
    bool Has(string path);

    /// <summary>Снять метку; вызывается только после проверки подлинности (FR-17).</summary>
    void Remove(string path);

    /// <summary>Содержимое Zone.Identifier (HostUrl, ReferrerUrl) — откуда скачан файл (FR-102); null — метки нет.</summary>
    string? Read(string path) => null;
}

/// <summary>Штатный Rhino Installer Engine для <c>.rhi</c> (13a): <c>rhiexec.exe "&lt;файл&gt;"</c>.</summary>
public interface IRhinoInstallerEngine
{
    /// <summary>Путь к rhiexec.exe по ассоциации <c>.rhi</c>; null — не найден.</summary>
    string? Find();

    /// <summary>Запустить установку; выбор версий Rhino — в окне rhiexec.</summary>
    bool Launch(string rhiFile);
}

/// <summary>Наблюдение за папкой профиля (FR-55a). Обработчик вызывается из потока наблюдателя.</summary>
public interface IDirectoryWatcher
{
    /// <summary>
    /// Следить за <paramref name="directory"/> с подпапками. <c>overflow = true</c> — буфер переполнен или наблюдатель
    /// дал ошибку: изменения могли потеряться, нужно полное пересканирование. Несуществующая папка — null.
    /// </summary>
    IDisposable? Watch(string directory, Action<bool> changed);
}

/// <summary>Текстовый редактор для файла hosts (FR-88): имя, путь к exe, необязательные аргументы.</summary>
public sealed record TextEditor(string Name, string Path, string? Arguments = null);

public enum ElevationResult
{
    Ok,

    /// <summary>Пользователь отказался в окне UAC.</summary>
    Declined,

    Failed,
}

/// <summary>Запуск редактора: результат и задача, завершающаяся при закрытии редактора (FR-89g).</summary>
public sealed record EditorLaunch(ElevationResult Result, Task Exited, string? Error = null);

/// <summary>Файл hosts (14.2): путь, поиск редакторов, запуск с правами администратора, восстановление.</summary>
public interface IHostsPlatform
{
    /// <summary><c>%SystemRoot%\System32\drivers\etc\hosts</c> — вычисляется, не зашит (FR-87).</summary>
    string HostsPath { get; }

    /// <summary>Блокнот всегда; Notepad++, VS Code, Sublime Text, Notepad3 и другие — при наличии (FR-88).</summary>
    IReadOnlyList<TextEditor> FindEditors();

    /// <summary>Открыть <paramref name="file"/> в редакторе; <paramref name="elevated"/> — с правами администратора (FR-89a).</summary>
    EditorLaunch OpenEditor(TextEditor editor, string file, bool elevated);

    /// <summary>Восстановить hosts из копии вспомогательным процессом с правами администратора (FR-89e).</summary>
    Task<ElevationResult> RestoreElevatedAsync(string backupPath);
}
