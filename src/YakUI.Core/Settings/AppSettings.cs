using YakUI.Core.Compat;

namespace YakUI.Core.Settings;

/// <summary>Настройки YakUI (JSON в папке настроек, AD-06).</summary>
/// <summary>Тема оформления (FR-83).</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>Язык интерфейса (FR-82): System — русский для русской Windows, иначе английский.</summary>
public enum AppLanguage
{
    System,
    Russian,
    English,
}

/// <summary>Выбранный редактор hosts (FR-89): путь к exe и необязательные аргументы.</summary>
public sealed record HostsEditorSetting(string Path, string? Arguments = null);

/// <summary>Пользовательский корень установки с понятным именем (FR-41).</summary>
public sealed record CustomRoot(string Name, string Path);

public sealed class AppSettings
{
    /// <summary>Пользовательские корни установки, в том числе сетевые (FR-41).</summary>
    public List<CustomRoot>? CustomRoots { get; set; }

    /// <summary>Последний пользовательский путь по цели: ключ цели → путь (FR-40).</summary>
    public Dictionary<string, string>? LastCustomRoot { get; set; }

    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;

    /// <summary>Major-версии Rhino с Yak (FR-81b); пополняется из тегов каталога.</summary>
    public List<int>? RhinoMajors { get; set; }

    /// <summary>Таблица сред .NET по версиям Rhino (17a).</summary>
    public List<DotNetRuntimeRule>? DotNetRuntimes { get; set; }

    public string? ApiBaseUrl { get; set; }

    /// <summary>Лимит параллельных запросов к API (FR-20).</summary>
    public int ApiParallelism { get; set; }

    /// <summary>Сколько индекс <c>/packages</c> считается свежим, минут.</summary>
    public int CatalogIndexTtlMinutes { get; set; }

    /// <summary>yak.exe, указанные пользователем вручную (6.1, п. 3).</summary>
    public List<string>? ManualYakPaths { get; set; }

    /// <summary>Закреплённый yak для major-версии Rhino: ключ — major, значение — путь (FR-13).</summary>
    public Dictionary<int, string>? PinnedYak { get; set; }

    /// <summary>yak, использовавшийся до скачивания нового, по версиям Rhino — «Вернуть предыдущий» (FR-19).</summary>
    public Dictionary<int, string>? PreviousYak { get; set; }

    /// <summary>Последняя проверка новых сборок yak (FR-12: при запуске не чаще раза в сутки).</summary>
    public DateTimeOffset? LastYakCheck { get; set; }

    /// <summary>Мастер первого запуска пройден или пропущен (FR-18).</summary>
    public bool FirstRunDone { get; set; }

    /// <summary>SHA-256 неподписанных yak.exe, явно подтверждённых пользователем (FR-17).</summary>
    public List<string>? ConfirmedUnsignedYakSha256 { get; set; }

    /// <summary>Последний выбранный пункт списка Rhino (<see cref="Targets.TargetChoice.Key"/>, FR-81f).</summary>
    public string? LastTargetKey { get; set; }

    /// <summary>Выбранный сервис-релиз неустановленной версии; null — «последний» (FR-81c, FR-81f).</summary>
    public int? LastServiceRelease { get; set; }

    /// <summary>Уровень текстового лога: Debug, Info, Warning, Error (NFR-08).</summary>
    public string? LogLevel { get; set; }

    /// <summary>Папка загрузок .yak (FR-35); null — <c>&lt;данные YakUI&gt;\downloads</c>.</summary>
    public string? DownloadsDirectory { get; set; }

    /// <summary>Правила типа выпуска Rhino (FR-81g): правятся без пересборки.</summary>
    public List<YakUI.Core.Targets.ReleaseTypeRule>? ReleaseTypeRules { get; set; }

    /// <summary>Тема оформления (FR-83): System — как в Windows.</summary>
    public AppTheme Theme { get; set; }

    /// <summary>Редактор файла hosts (FR-89); null — при следующем «Открыть hosts» спросить.</summary>
    public HostsEditorSetting? HostsEditor { get; set; }

    /// <summary>Контрольные строки hosts «IP имя» (FR-89h); по умолчанию пусто.</summary>
    public List<string>? HostsWatchList { get; set; }

    /// <summary>Язык интерфейса (FR-82); применяется после перезапуска.</summary>
    public AppLanguage Language { get; set; }

    /// <summary>Прокси (FR-95): по умолчанию «Системный».</summary>
    public YakUI.Core.Net.ProxySettings? Proxy { get; set; }

    public string ResolveDownloadsDirectory(string dataDirectory) =>
        string.IsNullOrWhiteSpace(DownloadsDirectory) ? Path.Combine(dataDirectory, "downloads") : DownloadsDirectory;

    public static AppSettings CreateDefault()
    {
        var s = new AppSettings();
        s.Normalize();
        return s;
    }

    /// <summary>Заполняет отсутствующие разделы значениями по умолчанию (файл старой версии, ручная правка).</summary>
    public void Normalize()
    {
        RhinoMajors ??= [6, 7, 8, 9];
        if (DotNetRuntimes is null || DotNetRuntimes.Count == 0)
            DotNetRuntimes = [.. DotNetRuntimeTable.Defaults];
        if (string.IsNullOrWhiteSpace(ApiBaseUrl))
            ApiBaseUrl = "https://yak.rhino3d.com";
        if (ApiParallelism <= 0)
            ApiParallelism = 6;
        if (CatalogIndexTtlMinutes <= 0)
            CatalogIndexTtlMinutes = 60;
        ManualYakPaths ??= [];
        PinnedYak ??= [];
        PreviousYak ??= [];
        CustomRoots ??= [];
        LastCustomRoot ??= [];
        ConfirmedUnsignedYakSha256 ??= [];
        if (string.IsNullOrWhiteSpace(LogLevel))
            LogLevel = "Info";
        Proxy ??= new YakUI.Core.Net.ProxySettings();
        if (ReleaseTypeRules is null || ReleaseTypeRules.Count == 0)
            ReleaseTypeRules = [.. YakUI.Core.Targets.ReleaseTypeRules.Defaults];
    }
}
