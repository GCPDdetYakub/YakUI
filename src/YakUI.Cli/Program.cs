using System.Globalization;
using System.Text;
using YakUI.Core.Compat;
using YakUI.Core.Cli;
using YakUI.Core.Diagnostics;
using YakUI.Core.Install;
using YakUI.Core.Logging;
using YakUI.Core.Platform;
using YakUI.Core.Settings;
using YakUI.Core.Storage;
using YakUI.Core.Targets;
using YakUI.Core.Tools;
using YakUI.Platform.Windows;

namespace YakUI.Cli;

/// <summary>
/// Консольный режим YakUI (AD-05, FR-73 — частично). Этап 1: <c>env</c>, <c>list</c>.
/// Коды возврата: 0 — успех, 1 — ошибка выполнения, 2 — неверные аргументы.
/// </summary>
public static class Program
{
    public const int ExitOk = 0;
    public const int ExitError = 1;
    public const int ExitUsage = 2;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        // Консоль YakUI — на русском (FR-82 относится к окну); сообщения ядра — на том же языке.
        YakUI.Core.Localization.UiLanguage.Apply(YakUI.Core.Settings.AppLanguage.Russian);

        // FR-95: прокси из настроек — для всех запросов CLI и для yak.
        YakUI.Core.Net.SwitchableProxy.Shared.Settings = new SettingsStore(new WindowsPlatform().Paths.ConfigDirectory).Load().Proxy ?? new();

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? ExitUsage : ExitOk;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "env" => await EnvAsync(args[1..], cts.Token),
                "list" => await ListAsync(args[1..], cts.Token),
                "catalog" => await CatalogAsync(args[1..], cts.Token),
                "search" => Search(args[1..]),
                "inspect" => await InspectAsync(args[1..], cts.Token),
                "icons" => await IconsAsync(args[1..], cts.Token),
                "download" => await DownloadAsync(args[1..], cts.Token),
                "install" => await InstallAsync(args[1..], cts.Token),
                "uninstall" when args.Contains("--root") => await UninstallCustomAsync(args[1], int.Parse(args[Array.IndexOf(args, "--target") + 1], CultureInfo.InvariantCulture), args[Array.IndexOf(args, "--root") + 1], cts.Token),
                "uninstall" => await UninstallAsync(args[1..], cts.Token),
                "import" => await ImportAsync(args[1..], cts.Token),
                "update" => await UpdateAsync(args[1..], cts.Token),
                "update-all" => await UpdateAllAsync(args[1..], cts.Token),
                "pin" => await PinAsync(args[1..], pin: true, cts.Token),
                "unpin" => await PinAsync(args[1..], pin: false, cts.Token),
                "yak-check" => await YakCheckAsync(cts.Token),
                "yak-download" => await YakDownloadAsync(args[1..], cts.Token),
                _ => Usage($"Неизвестная команда «{args[0]}»."),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Отменено.");
            return ExitError;
        }
        catch (YakUI.Core.Install.InstallFailedException e)
        {
            Console.Error.WriteLine($"Установка не выполнена: {e.Message}");
            return ExitError;
        }
        catch (DatabaseBusyException e)
        {
            Console.Error.WriteLine(e.Message);
            return ExitError;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Ошибка: {e.Message}");
            return ExitError;
        }
    }

    private static async Task<int> EnvAsync(string[] args, CancellationToken ct)
    {
        string? outFile = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length)
                outFile = args[++i];
            else
                return Usage($"Неизвестный аргумент «{args[i]}».");
        }

        using var app = await Composition.CreateAsync(ct);
        var report = EnvironmentReport.Build(app.Targets, app.Library.Tools, app.Library.SelectFor, app.Platform.Paths, app.Settings, DateTimeOffset.Now);

        // ОВ-19 (21.5): галочка «Automatically update packages when Rhino starts».
        var autoUpdate = new RhinoAutoUpdateReader(app.Platform.FileSystem, app.Platform.Paths);
        report += Environment.NewLine + "Автообновление пакетов Rhino (ОВ-19)" + Environment.NewLine
            + string.Concat(app.Targets.Select(t => $"  Rhino {t.Major}: {autoUpdate.Read(t.Major) switch { AutoUpdateState.Enabled => "включено", AutoUpdateState.Disabled => "выключено", _ => "нет такой настройки" }}{Environment.NewLine}"));

        if (outFile is null)
        {
            Console.Write(report);
        }
        else
        {
            File.WriteAllText(outFile, report, new UTF8Encoding(true));
            Console.WriteLine($"Отчёт сохранён: {Path.GetFullPath(outFile)}");
        }

        return ExitOk;
    }

    private static async Task<int> ListAsync(string[] args, CancellationToken ct)
    {
        int? major = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--target" && i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var m))
            {
                major = m;
                i++;
            }
            else
            {
                return Usage($"Неверный аргумент «{args[i]}». Ожидается: list [--target <major>]");
            }
        }

        using var app = await Composition.CreateAsync(ct);
        var targets = app.Targets.Where(t => major is null || t.Major == major).ToList();
        if (targets.Count == 0)
        {
            Console.Error.WriteLine(major is null ? "Установленные Rhino 6+ не найдены." : $"Rhino {major} не установлен.");
            return ExitError;
        }

        var service = new InstalledPackagesService(new ProfileScanner(app.Platform.FileSystem), app.Library);
        foreach (var target in targets)
            Print(await service.GetAsync(target, ct));

        return ExitOk;
    }

    /// <summary>Построение или обновление кэша каталога краулером (FR-20, FR-21); <c>--rebuild</c> — с нуля.</summary>
    private static async Task<int> CatalogAsync(string[] args, CancellationToken ct)
    {
        var rebuild = false;
        foreach (var a in args)
        {
            if (a == "--rebuild")
                rebuild = true;
            else
                return Usage($"Неизвестный аргумент «{a}». Ожидается: catalog [--rebuild]");
        }

        var platform = new WindowsPlatform();
        var settings = new SettingsStore(platform.Paths.ConfigDirectory).Load();
        using var db = OpenDatabase(platform.Paths);
        var log = new FileLog(Path.Combine(platform.Paths.DataDirectory, "logs"), FileLog.ParseLevel(settings.LogLevel));
        using var api = new YakUI.Core.Api.YakApiClient(new YakUI.Core.Api.YakApiOptions { BaseUri = new Uri(settings.ApiBaseUrl!), MaxParallelism = settings.ApiParallelism });
        var catalog = new YakUI.Core.Catalog.CatalogService(api, db, TimeSpan.FromMinutes(settings.CatalogIndexTtlMinutes));

        if (rebuild)
        {
            if (SingleInstance.IsAppRunning(platform.Paths.DataDirectory))
            {
                Console.Error.WriteLine("Открыто окно YakUI: полная пересборка кэша возможна только при закрытом окне (NFR-05a). Без --rebuild кэш обновляется и при открытом окне.");
                return ExitError;
            }

            catalog.ClearCache();
            Console.WriteLine("Кэш каталога очищен.");
        }

        var crawler = new YakUI.Core.Catalog.CatalogCrawler(catalog, new YakUI.Core.Catalog.CrawlerOptions { MaxConcurrency = settings.ApiParallelism }, log);
        var lastLine = DateTime.MinValue;
        var progress = new SyncProgress<YakUI.Core.Catalog.CrawlProgress>(p =>
        {
            if ((DateTime.UtcNow - lastLine).TotalSeconds < 5 && p.Done != p.Total)
                return;
            lastLine = DateTime.UtcNow;
            Console.WriteLine($"  {p.Done}/{p.Total}, ошибок {p.Failed}, потоков {p.Concurrency}{(p.Throttled ? ", пауза (429/503)" : "")}");
        });

        var r = await crawler.RunAsync(progress, ct);
        var (packages, withVersions, distributions) = catalog.GetCacheStats();
        var releases = catalog.GetKnownRhinoReleases();

        Console.WriteLine();
        Console.WriteLine($"Пакетов в каталоге:        {packages}");
        Console.WriteLine($"С загруженными версиями:   {withVersions}");
        Console.WriteLine($"Дистрибутивов:             {distributions}");
        Console.WriteLine($"Загружено в этом запуске:  {r.Fetched} из {r.Pending}");
        Console.WriteLine($"Ошибок:                    {r.Failed}{(r.Failed > 0 ? " (" + string.Join(", ", r.FailedNames.Take(10)) + ")" : "")}");
        Console.WriteLine($"Замедлений (429/503):      {r.ThrottleEvents}");
        Console.WriteLine($"Время:                     {r.Duration:mm\\:ss\\.f}");
        foreach (var g in releases.GroupBy(x => x.Major))
            Console.WriteLine($"Rhino {g.Key}: сервис-релизы в тегах — {string.Join(", ", g.Select(x => x.Minor))}");

        return r.Failed == 0 ? ExitOk : ExitError;
    }

    /// <summary>
    /// Каталог с теми же фильтрами, что и экран «Каталог» (для сверки с <c>_PackageManager</c>, критерий этапа 2).
    /// По умолчанию: статусы Нативный + Совместимый снизу + Без ограничений + Неизвестно, платформа Windows, пре-релизы скрыты.
    /// </summary>
    private static int Search(string[] args)
    {
        string? query = null;
        RhinoRelease? target = null;
        var filter = new YakUI.Core.Catalog.CatalogFilter();
        var namesOnly = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--target" when i + 1 < args.Length:
                    var t = args[++i].Split('.');
                    if (!int.TryParse(t[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major))
                        return Usage("--target: ожидается major или major.minor, например 8 или 7.14");
                    var minor = t.Length > 1 && int.TryParse(t[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : (int?)null;
                    target = new RhinoRelease(major, minor ?? int.MaxValue);
                    break;
                case "--query" when i + 1 < args.Length:
                    query = args[++i];
                    break;
                case "--all":
                    filter = filter with { Statuses = Enum.GetValues<CompatibilityStatus>().ToHashSet() };
                    break;
                case "--prerelease":
                    filter = filter with { HidePrerelease = false };
                    break;
                case "--platform" when i + 1 < args.Length:
                    filter = filter with
                    {
                        Platforms = args[++i] switch
                        {
                            "win" => new HashSet<YakUI.Core.Catalog.CatalogPlatform> { YakUI.Core.Catalog.CatalogPlatform.Windows },
                            "mac" => new HashSet<YakUI.Core.Catalog.CatalogPlatform> { YakUI.Core.Catalog.CatalogPlatform.Mac },
                            _ => new HashSet<YakUI.Core.Catalog.CatalogPlatform>(),
                        },
                    };
                    break;
                case "--names":
                    namesOnly = true;
                    break;
                case "--sort" when i + 1 < args.Length:
                    filter = filter with
                    {
                        Sort = args[++i] switch
                        {
                            "name" => YakUI.Core.Catalog.CatalogSort.Name,
                            "month" => YakUI.Core.Catalog.CatalogSort.DownloadsLastMonth,
                            "updated" => YakUI.Core.Catalog.CatalogSort.Updated,
                            "created" => YakUI.Core.Catalog.CatalogSort.Created,
                            _ => YakUI.Core.Catalog.CatalogSort.Downloads,
                        },
                        Descending = args[i] != "name",
                    };
                    break;
                default:
                    return Usage($"Неверный аргумент «{args[i]}».");
            }
        }

        var platform = new WindowsPlatform();
        var settings = new SettingsStore(platform.Paths.ConfigDirectory).Load();
        using var db = OpenDatabase(platform.Paths);
        using var api = new YakUI.Core.Api.YakApiClient(new YakUI.Core.Api.YakApiOptions { BaseUri = new Uri(settings.ApiBaseUrl!) });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = new YakUI.Core.Catalog.CatalogService(api, db, TimeSpan.FromMinutes(settings.CatalogIndexTtlMinutes)).LoadSnapshot();
        var loadTime = sw.Elapsed;
        if (snapshot.Packages.Count == 0)
        {
            Console.Error.WriteLine("Кэш каталога пуст. Выполните: YakUI.Cli catalog");
            return ExitError;
        }

        // «--target 8» без minor — последний сервис-релиз установленного Rhino 8, иначе «бесконечный» minor.
        var targets = new RhinoDiscovery(platform.Registry, platform.FileSystem, platform.Paths, new DotNetRuntimeTable(settings.DotNetRuntimes!), settings.ReleaseTypeRules).Discover();
        if (target is { Minor: int.MaxValue } open && targets.FirstOrDefault(x => x.Major == open.Major) is { } installedTarget)
            target = installedTarget.Release;

        var choice = target is { } tr && targets.FirstOrDefault(x => x.Release == tr) is { } exact
            ? TargetChoice.Installed(exact)
            : target is null ? TargetChoice.Any : null;
        var installed = new InstalledVersions(new ProfileScanner(platform.FileSystem)).For(choice, targets);

        sw.Restart();
        var result = YakUI.Core.Catalog.CatalogQuery.Run(snapshot, filter with { Text = query, Target = target }, installed);
        var queryTime = sw.Elapsed;

        if (namesOnly)
        {
            foreach (var e in result.Entries.OrderBy(e => e.Package.Name, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine(e.Package.Name);
            return ExitOk;
        }

        Console.WriteLine($"Цель: {(target is { } r ? "Rhino " + r : "любая версия (без фильтра)")}; запрос: {query ?? "—"}");
        Console.WriteLine($"Найдено {result.Entries.Count} из {result.TotalPackages} (уточняется {result.PendingPackages}); снимок {loadTime.TotalMilliseconds:0} мс, запрос {queryTime.TotalMilliseconds:0} мс");
        Console.WriteLine();
        var width = Math.Min(36, result.Entries.Select(e => e.Package.Name.Length).DefaultIfEmpty(10).Max());
        foreach (var e in result.Entries.Take(200))
        {
            var status = e.IsPending ? "уточняется" : e.Status?.ToString() ?? "—";
            var inst = e.InstalledVersion is null ? "" : $"  [установлен {e.InstalledVersion}{(e.HasUpdate ? " → есть обновление" : "")}]";
            Console.WriteLine($"  {e.Package.Name.PadRight(width)}  {e.TargetVersion,-22} {status,-18} {e.Package.DownloadCount,9}{inst}");
        }

        if (result.Entries.Count > 200)
            Console.WriteLine($"  … ещё {result.Entries.Count - 200}");
        return ExitOk;
    }

    /// <summary>
    /// Скачать .yak в папку загрузок (FR-36, FR-37): версия (по умолчанию самая новая не-prerelease), лучший дистрибутив
    /// для установленного Rhino той же major или первый не-Mac; <c>--all</c> — все дистрибутивы версии.
    /// </summary>
    private static async Task<int> DownloadAsync(string[] args, CancellationToken ct)
    {
        var all = args.Contains("--all");
        int? major = null;
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--all")
                continue;
            if (args[i] == "--target" && i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var m))
            {
                major = m;
                i++;
                continue;
            }

            positional.Add(args[i]);
        }

        if (positional.Count is < 1 or > 2)
            return Usage("Ожидается: download <пакет> [версия] [--all] [--target <major>]");

        var platform = new WindowsPlatform();
        var settings = new SettingsStore(platform.Paths.ConfigDirectory).Load();
        using var db = OpenDatabase(platform.Paths);
        using var api = new YakUI.Core.Api.YakApiClient(new YakUI.Core.Api.YakApiOptions { BaseUri = new Uri(settings.ApiBaseUrl!) });
        var catalog = new YakUI.Core.Catalog.CatalogService(api, db, TimeSpan.FromMinutes(settings.CatalogIndexTtlMinutes));
        var versions = (await catalog.GetVersionsAsync(positional[0], ct)).Value;
        var version = positional.Count == 2
            ? versions.FirstOrDefault(v => v.Version == positional[1])
            : versions.Where(v => !v.Prerelease).MaxBy(v => YakUI.Core.Versioning.PackageVersion.TryParse(v.Version, out var pv) ? pv : null,
                Comparer<YakUI.Core.Versioning.PackageVersion?>.Create((a, b) => a is null ? (b is null ? 0 : -1) : a.CompareTo(b)));
        if (version is null)
        {
            Console.Error.WriteLine($"Версия не найдена: {positional[0]} {(positional.Count == 2 ? positional[1] : "")}");
            return ExitError;
        }

        var targets = new RhinoDiscovery(platform.Registry, platform.FileSystem, platform.Paths, new DotNetRuntimeTable(settings.DotNetRuntimes!), settings.ReleaseTypeRules).Discover();
        var release = targets.FirstOrDefault(t => major is null || t.Major == major)?.Release;
        var dists = all
            ? version.Distributions.ToList()
            : new[] { (release is { } r ? CompatibilityRules.SelectBest(version.Distributions, d => d.Tag, r) : null)
                      ?? version.Distributions.FirstOrDefault(d => d.Tag.Platform != DistributionPlatform.Mac) }.OfType<YakUI.Core.Api.PackageDistribution>().ToList();
        if (dists.Count == 0)
        {
            Console.Error.WriteLine("Нет подходящего дистрибутива.");
            return ExitError;
        }

        var log = new FileLog(Path.Combine(platform.Paths.DataDirectory, "logs"), FileLog.ParseLevel(settings.LogLevel));
        using var downloader = new YakUI.Core.Downloads.Downloader(settings.ResolveDownloadsDirectory(platform.Paths.DataDirectory),
            new YakUI.Core.Downloads.DownloadRegistry(db), log: log, journal: new OperationJournal(db));
        var queue = new YakUI.Core.Downloads.DownloadQueue(downloader);
        var lastPrint = DateTime.MinValue;
        queue.Changed += item =>
        {
            if ((DateTime.UtcNow - lastPrint).TotalSeconds < 2 && !item.IsFinished)
                return;
            lastPrint = DateTime.UtcNow;
            var pct = item.BytesTotal is > 0 ? $" {100.0 * item.BytesDone / item.BytesTotal:0}%" : "";
            Console.WriteLine($"  {item.Request.Filename}: {item.State}{pct}{(item.Error is null ? "" : " — " + item.Error)}");
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var items = dists.Where(d => d.Url is not null).Select(d => queue.Enqueue(new YakUI.Core.Downloads.DownloadRequest(
            d.Url!, d.Filename ?? Path.GetFileName(new Uri(d.Url!).LocalPath), positional[0], version.Version, d.Tag.Original, d.Size, version.CreatedAt))).ToList();
        await Task.WhenAll(items.Select(queue.Completion));

        foreach (var item in items)
        {
            if (item.Record is { } rec)
                Console.WriteLine($"{item.State}: {rec.Path}  {rec.Size / 1024.0 / 1024:0.0} МБ  SHA-256 {rec.Sha256}");
        }

        Console.WriteLine($"Время: {sw.Elapsed:mm\\:ss\\.f}");
        return items.All(i => i.State is YakUI.Core.Downloads.DownloadState.Completed or YakUI.Core.Downloads.DownloadState.AlreadyDownloaded) ? ExitOk : ExitError;
    }

    /// <summary>
    /// Стандартная установка (AD-03, FR-40, FR-50): лучший дистрибутив по разделу 17 для установленной цели,
    /// файл из папки загрузок или скачанный, затем <see cref="YakUI.Core.Install.StandardInstaller"/>.
    /// </summary>
    private static async Task<int> InstallAsync(string[] args, CancellationToken ct)
    {
        var forceBuiltIn = false;
        var unpack = false;
        string? root = null;
        string? runtimeArg = null;
        int? major = null;
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--builtin")
                forceBuiltIn = true;
            else if (args[i] == "--unpack")
                unpack = true;
            else if (args[i] == "--root" && i + 1 < args.Length)
                root = args[++i];
            else if (args[i] == "--runtime" && i + 1 < args.Length)
                runtimeArg = args[++i];
            else if (args[i] == "--target" && i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var m))
            {
                major = m;
                i++;
            }
            else
                positional.Add(args[i]);
        }

        if (positional.Count is < 1 or > 2 || major is null)
            return Usage("Ожидается: install <пакет> [версия] --target <major> [--builtin] [--root <папка> [--unpack]]");
        if (unpack && root is null)
            return Usage("--unpack требует --root <папка> (FR-45).");

        using var app = await Composition.CreateAsync(ct);
        var target = app.Targets.Where(t => t.Major == major).MaxBy(t => t.Release);
        if (target is null)
        {
            Console.Error.WriteLine($"Rhino {major} не установлен — установка в стандартный профиль недоступна (FR-81e).");
            return ExitError;
        }

        using var api = new YakUI.Core.Api.YakApiClient(new YakUI.Core.Api.YakApiOptions { BaseUri = new Uri(app.Settings.ApiBaseUrl!) });
        var catalog = new YakUI.Core.Catalog.CatalogService(api, app.Database, TimeSpan.FromMinutes(app.Settings.CatalogIndexTtlMinutes));
        var versions = (await catalog.GetVersionsAsync(positional[0], ct)).Value;
        var version = positional.Count == 2
            ? versions.FirstOrDefault(v => v.Version == positional[1])
            : versions.Where(v => !v.Prerelease && CompatibilityRules.SelectBest(v.Distributions, d => d.Tag, target.Release) is not null)
                .MaxBy(v => YakUI.Core.Versioning.PackageVersion.TryParse(v.Version, out var pv) ? pv : null,
                    Comparer<YakUI.Core.Versioning.PackageVersion?>.Create((a, b) => a is null ? (b is null ? 0 : -1) : a.CompareTo(b)));
        if (version is null)
        {
            Console.Error.WriteLine($"Подходящая версия не найдена: {string.Join(' ', positional)}");
            return ExitError;
        }

        var dist = CompatibilityRules.SelectBest(version.Distributions, d => d.Tag, target.Release);
        if (dist?.Url is null)
        {
            Console.Error.WriteLine($"У {positional[0]} {version.Version} нет дистрибутива, совместимого с {target.DisplayName}.");
            return ExitError;
        }

        var log = new FileLog(Path.Combine(app.Platform.Paths.DataDirectory, "logs"), FileLog.ParseLevel(app.Settings.LogLevel));
        var journal = new OperationJournal(app.Database);
        using var downloader = new YakUI.Core.Downloads.Downloader(app.Settings.ResolveDownloadsDirectory(app.Platform.Paths.DataDirectory),
            new YakUI.Core.Downloads.DownloadRegistry(app.Database), log: log, journal: journal);
        var queue = new YakUI.Core.Downloads.DownloadQueue(downloader);
        var item = queue.Enqueue(new YakUI.Core.Downloads.DownloadRequest(dist.Url, dist.Filename ?? Path.GetFileName(new Uri(dist.Url).LocalPath),
            positional[0], version.Version, dist.Tag.Original, dist.Size, version.CreatedAt));
        await queue.Completion(item);
        if (item.Record is not { } rec)
        {
            Console.Error.WriteLine($"Не удалось скачать: {item.Error}");
            return ExitError;
        }

        Console.WriteLine($"Файл: {rec.Path} ({item.State})");
        if (root is not null)
        {
            // Пользовательский путь (FR-42…FR-45).
            var custom = CustomInstallerFor(app, journal);
            // --runtime netfx|netcore: среда для ссылки .ghlink у пакетов с net48 и net7.0/net8.0 (FR-43, 21.6).
            YakUI.Core.Compat.DotNetRuntimeKind? runtime = runtimeArg switch
            {
                "netfx" => YakUI.Core.Compat.DotNetRuntimeKind.NetFramework,
                "netcore" => YakUI.Core.Compat.DotNetRuntimeKind.NetCore,
                _ => null,
            };
            var cr = custom.Install(new YakUI.Core.Install.CustomInstallRequest(target, Path.GetFullPath(root), positional[0], version.Version,
                unpack ? YakUI.Core.Install.InstallMode.Unpack : YakUI.Core.Install.InstallMode.Custom, YakFile: rec.Path, Sha256: rec.Sha256, Runtime: runtime));
            PrintCustom(cr);
            return ExitOk;
        }

        var installer = new YakUI.Core.Install.StandardInstaller(app.Library, new YakCli(app.Platform.Processes, journal),
            new ProfileScanner(app.Platform.FileSystem), new YakUI.Core.Install.InstallRegistry(app.Database), app.Platform.RhinoProcesses, journal, log);
        var result = await installer.InstallAsync(new YakUI.Core.Install.InstallRequest(target, positional[0], version.Version, rec.Path, dist.Tag, rec.Sha256, forceBuiltIn), ct);

        Console.WriteLine($"Установлено: {result.Name} {result.Version} → {result.VersionDirectory}");
        Console.WriteLine($"Способ: {(result.Method == YakUI.Core.Install.InstallMethod.Yak ? "yak" : "встроенный установщик")}; yak list: {(result.VerifiedByYakList ? "подтверждено" : "не подтверждено")}");
        if (result.YakFailure is not null)
            Console.WriteLine($"Причина перехода на встроенный: {result.YakFailure}");
        foreach (var w in result.Warnings)
            Console.WriteLine($"Внимание: {w}");
        if (result.PendingRestart)
            Console.WriteLine($"{target.DisplayName} запущен — пакет загрузится после перезапуска.");
        return ExitOk;
    }

    private static (List<string> Positional, int? Major, string? Only) ParseTargetArgs(string[] args)
    {
        int? major = null;
        string? only = null;
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--target" && i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var m))
            {
                major = m;
                i++;
            }
            else if (args[i] == "--only" && i + 1 < args.Length)
            {
                only = args[++i];
            }
            else
            {
                positional.Add(args[i]);
            }
        }

        return (positional, major, only);
    }

    private sealed class CliPackageFiles(YakUI.Core.Catalog.CatalogService catalog, YakUI.Core.Downloads.Downloader downloader) : YakUI.Core.Install.IPackageFiles
    {
        public async Task<IReadOnlyList<YakUI.Core.Api.PackageVersionInfo>> GetVersionsAsync(string name, CancellationToken ct) =>
            (await catalog.GetVersionsAsync(name, ct)).Value;

        public async Task<YakUI.Core.Downloads.DownloadRecord> GetFileAsync(YakUI.Core.Downloads.DownloadRequest request, CancellationToken ct) =>
            downloader.FindExisting(request.Url) ?? await downloader.DownloadAsync(request, null, ct);
    }

    /// <summary>Жизненный цикл пакетов для CLI (этап 4) поверх <see cref="Composition"/>.</summary>
    private static (YakUI.Core.Install.PackageLifecycle Lifecycle, IDisposable Owned) Lifecycle(Composition app)
    {
        var log = new FileLog(Path.Combine(app.Platform.Paths.DataDirectory, "logs"), FileLog.ParseLevel(app.Settings.LogLevel));
        var journal = new OperationJournal(app.Database);
        var api = new YakUI.Core.Api.YakApiClient(new YakUI.Core.Api.YakApiOptions { BaseUri = new Uri(app.Settings.ApiBaseUrl!) });
        var catalog = new YakUI.Core.Catalog.CatalogService(api, app.Database, TimeSpan.FromMinutes(app.Settings.CatalogIndexTtlMinutes));
        var downloader = new YakUI.Core.Downloads.Downloader(app.Settings.ResolveDownloadsDirectory(app.Platform.Paths.DataDirectory),
            new YakUI.Core.Downloads.DownloadRegistry(app.Database), log: log, journal: journal);
        var scanner = new ProfileScanner(app.Platform.FileSystem);
        var registry = new YakUI.Core.Install.InstallRegistry(app.Database);
        var installer = new YakUI.Core.Install.StandardInstaller(app.Library, new YakCli(app.Platform.Processes, journal), scanner, registry, app.Platform.RhinoProcesses, journal, log);
        var pending = new YakUI.Core.Install.PendingRemovals(app.Database, app.Platform.RhinoProcesses, journal);
        foreach (var removed in pending.Process())
            Console.WriteLine($"Отложенное удаление выполнено: {removed}");
        var changer = new YakUI.Core.Install.VersionChanger(installer, scanner, registry, app.Platform.RhinoProcesses, journal, log, pending);
        var lifecycle = new YakUI.Core.Install.PackageLifecycle(new CliPackageFiles(catalog, downloader), changer, scanner, new YakUI.Core.Install.PinRegistry(app.Database), journal);
        return (lifecycle, new Disposables(api, downloader));
    }

    private sealed class Disposables(params IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var i in items)
                i.Dispose();
        }
    }

    private static RhinoTarget? TargetOrError(Composition app, int major)
    {
        var target = app.Targets.Where(t => t.Major == major).MaxBy(t => t.Release);
        if (target is null)
            Console.Error.WriteLine($"Rhino {major} не установлен.");
        return target;
    }

    /// <summary>Смена версии пакета (FR-49): <c>update &lt;пакет&gt; [версия] --target N</c>.</summary>
    private static async Task<int> UpdateAsync(string[] args, CancellationToken ct)
    {
        var (positional, major, _) = ParseTargetArgs(args);
        if (positional.Count is < 1 or > 2 || major is null)
            return Usage("Ожидается: update <пакет> [версия] --target <major>");
        using var app = await Composition.CreateAsync(ct);
        if (TargetOrError(app, major.Value) is not { } target)
            return ExitError;
        var (lifecycle, owned) = Lifecycle(app);
        using (owned)
        {
            var r = await lifecycle.ChangeVersionAsync(target, positional[0], positional.Count == 2 ? positional[1] : null, null, ct);
            Console.WriteLine($"{r.Name}: {r.OldVersion ?? "—"} → {r.NewVersion} ({(r.Method == YakUI.Core.Install.InstallMethod.Yak ? "yak" : "встроенный установщик")}); прежняя версия {(r.OldVersionRemoved ? "удалена" : "оставлена")}");
            foreach (var w in r.Warnings)
                Console.WriteLine($"Внимание: {w}");
        }

        return ExitOk;
    }

    /// <summary>«Обновить всё» (FR-59): <c>update-all --target N [--only a,b]</c>.</summary>
    private static async Task<int> UpdateAllAsync(string[] args, CancellationToken ct)
    {
        var (positional, major, only) = ParseTargetArgs(args);
        if (positional.Count != 0 || major is null)
            return Usage("Ожидается: update-all --target <major> [--only a,b]");
        using var app = await Composition.CreateAsync(ct);
        if (TargetOrError(app, major.Value) is not { } target)
            return ExitError;
        var (lifecycle, owned) = Lifecycle(app);
        using (owned)
        {
            var results = await lifecycle.UpdateAllAsync(target, only?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), null, ct);
            if (results.Count == 0)
                Console.WriteLine("Обновлять нечего.");
            foreach (var r in results)
                Console.WriteLine($"  {r.Outcome,-9} {r.Name} {r.From} → {r.To}{(r.Message is null ? "" : " — " + r.Message)}");
            return results.Any(r => r.Outcome == YakUI.Core.Install.BulkOutcome.Failed) ? ExitError : ExitOk;
        }
    }

    /// <summary>Закрепление версии (FR-58); в Rhino 8+ — предупреждение об автообновлении Rhino (ОВ-19).</summary>
    private static async Task<int> PinAsync(string[] args, bool pin, CancellationToken ct)
    {
        var (positional, major, _) = ParseTargetArgs(args);
        if (positional.Count != 1 || major is null)
            return Usage($"Ожидается: {(pin ? "pin" : "unpin")} <пакет> --target <major>");
        using var app = await Composition.CreateAsync(ct);
        if (TargetOrError(app, major.Value) is not { } target)
            return ExitError;
        var (lifecycle, owned) = Lifecycle(app);
        using (owned)
        {
            if (!pin)
            {
                lifecycle.Unpin(target, positional[0]);
                Console.WriteLine($"{positional[0]}: закрепление снято.");
                return ExitOk;
            }

            var p = lifecycle.Pin(target, positional[0]);
            Console.WriteLine($"{p.Name} {p.Version} закреплён в {target.DisplayName}.");
            if (new RhinoAutoUpdateReader(app.Platform.FileSystem, app.Platform.Paths).Read(target.Major) == AutoUpdateState.Enabled)
                Console.WriteLine("Внимание: в Rhino включено «Automatically update packages when Rhino starts» — при запуске Rhino может обновить пакет и нарушить закрепление (ОВ-19).");
        }

        return ExitOk;
    }

    /// <summary>Новые сборки yak по версиям Rhino (FR-12, 6.2).</summary>
    private static async Task<int> YakCheckAsync(CancellationToken ct)
    {
        using var app = await Composition.CreateAsync(ct);
        using var checker = new YakUI.Core.Tools.YakUpdateChecker(journal: new OperationJournal(app.Database));
        var majors = app.Targets.Select(t => t.Major).Concat(app.Library.Tools.Where(t => t.BoundRhinoMajor is not null).Select(t => t.BoundRhinoMajor!.Value));
        var r = await checker.CheckAsync(app.Library.Tools, majors, app.Library.SelectFor, ct);
        Console.WriteLine($"rid: {YakUI.Core.Tools.YakDownloader.Rid}");
        Console.WriteLine("Доступно на files.mcneel.com: " + (r.Available.Count == 0 ? "—" : string.Join(", ", r.Available.Select(a => $"{a.Version} ({(a.FoundBy == YakUI.Core.Tools.YakFoundBy.Documentation ? "документация" : "зондирование")}{(a.NewerThanDocs ? ", новее документации" : "")})"))));
        foreach (var l in r.Lines)
            Console.WriteLine($"  Rhino {l.RhinoMajor}: используется {l.Current?.Version?.ToString() ?? "—"}{(l.Current is null ? "" : $" ({l.Current.Path})")}; {(l.Newer is { } n ? $"доступна {n.Version}" : "новее нет")}");
        if (r.Problem is not null)
            Console.WriteLine($"Внимание: {r.Problem}");
        return ExitOk;
    }

    /// <summary>Скачать и проверить yak (FR-10, FR-14, FR-15, FR-17); хранение по 3 на версию Rhino (FR-11).</summary>
    private static async Task<int> YakDownloadAsync(string[] args, CancellationToken ct)
    {
        if (args.Length != 1)
            return Usage("Ожидается: yak-download <версия>, например 0.19.1");
        using var app = await Composition.CreateAsync(ct);
        var journal = new OperationJournal(app.Database);
        var registry = new YakUI.Core.Tools.YakDownloadRegistry(app.Database);
        using var downloader = new YakUI.Core.Tools.YakDownloader(app.Platform.Paths, app.Platform.Signatures, app.Platform.MarkOfTheWeb,
            new YakCli(app.Platform.Processes, journal), registry, journal: journal);
        var r = await downloader.DownloadAsync(args[0], null, ct);
        Console.WriteLine($"{(r.AlreadyPresent ? "Уже есть" : "Скачан")}: yak {r.Version} → {r.Path}");
        Console.WriteLine($"Подпись: {(r.Signed ? "Robert McNeel & Associates" : "нет (скачан YakUI по HTTPS с files.mcneel.com, SHA-256 записан)")}; привязка: Rhino {r.BoundRhinoMajor}");
        await app.Library.RefreshAsync(app.Targets, ct);
        var keep = (app.Settings.PinnedYak ?? []).Values.Concat(app.Targets.Select(t => app.Library.SelectFor(t.Major)?.Path).OfType<string>());
        foreach (var removed in downloader.Prune(app.Library.Tools, keep))
            Console.WriteLine($"Удалена старая сборка: {removed}");
        foreach (var t in app.Targets)
            Console.WriteLine($"  Rhino {t.Major}: используется {app.Library.SelectFor(t.Major)?.Version?.ToString() ?? "—"}");
        return ExitOk;
    }

    private static YakUI.Core.Install.CustomInstaller CustomInstallerFor(Composition app, OperationJournal journal) =>
        new(new YakUI.Core.Install.InstallRegistry(app.Database), app.Platform.RhinoProcesses, app.Platform.MarkOfTheWeb,
            new YakUI.Core.Install.PendingRemovals(app.Database, app.Platform.RhinoProcesses, journal), journal,
            rhinoInstallPaths: () => app.Targets.Select(t => t.InstallPath));

    private static void PrintCustom(YakUI.Core.Install.CustomInstallResult r)
    {
        Console.WriteLine($"{(r.Mode == YakUI.Core.Install.InstallMode.Unpack ? "Распаковано" : "Установлено")}: {r.Name} {r.Version} → {r.VersionDirectory}");
        if (r.GhLink is not null)
            Console.WriteLine($"Grasshopper: {r.GhLink} → {YakUI.Core.Install.GhLinks.ReadTarget(r.GhLink)}");
        foreach (var u in r.UserObjects)
            Console.WriteLine($"UserObjects: {u}");
        foreach (var rhp in r.RhinoPlugins)
            Console.WriteLine($".rhp (не зарегистрирован, FR-44): {rhp}");
        foreach (var w in r.Warnings)
            Console.WriteLine($"Внимание: {w}");
    }

    /// <summary>Импорт файла (FR-101…FR-103): <c>import &lt;файл&gt; --target N [--root папка [--unpack]]</c>.</summary>
    private static async Task<int> ImportAsync(string[] args, CancellationToken ct)
    {
        string? root = null;
        var unpack = false;
        var (positional, major, _) = ParseTargetArgs(args.Where((a, i) => !(a == "--root" || (i > 0 && args[i - 1] == "--root")) && a is not ("--unpack" or "--plan")).ToArray());
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--root" && i + 1 < args.Length)
                root = args[i + 1];
            if (args[i] == "--unpack")
                unpack = true;
        }

        if (positional.Count != 1 || major is null)
            return Usage("Ожидается: import <файл> --target <major> [--root <папка> [--unpack]]");
        using var app = await Composition.CreateAsync(ct);
        if (TargetOrError(app, major.Value) is not { } target)
            return ExitError;

        var file = Path.GetFullPath(positional[0]);
        var plan = YakUI.Core.Install.FileImport.Plan(file, app.Platform.FileSystem, app.Platform.MarkOfTheWeb);
        Console.WriteLine($"Формат: {plan.Kind}; пакет: {plan.Name} {plan.Version}; источник: {plan.Source}");
        if (plan.RuntimeFolders.Count > 0)
            Console.WriteLine($"Подпапки сред: {string.Join(", ", plan.RuntimeFolders)}");
        foreach (var dep in plan.Dependencies.Found)
            Console.WriteLine($"Зависимость рядом (будет скопирована): {Path.GetFileName(dep)}");
        if (plan.Dependencies.Missing.Count > 0)
            Console.WriteLine($"Не хватает: {string.Join(", ", plan.Dependencies.Missing)}. Плагин не загрузится. Импортируйте архив или папку пакета целиком.");
        if (args.Contains("--plan"))
            return ExitOk;
        if (plan.Kind == YakUI.Core.Install.ImportKind.RhinoInstaller)
        {
            Console.WriteLine(app.Platform.RhinoInstaller.Launch(file) ? "Запущен Rhino Installer Engine." : "rhiexec.exe не найден.");
            return ExitOk;
        }

        if (plan.Problem is { } problem)
        {
            Console.Error.WriteLine(problem);
            return ExitError;
        }

        var journal = new OperationJournal(app.Database);
        if (root is null)
        {
            if (!plan.StandardAllowed)
                return Usage("В стандартный профиль импортируется только .yak; для остальных форматов укажите --root <папка> (13a).");
            var log = new FileLog(Path.Combine(app.Platform.Paths.DataDirectory, "logs"), FileLog.ParseLevel(app.Settings.LogLevel));
            var installer = new YakUI.Core.Install.StandardInstaller(app.Library, new YakCli(app.Platform.Processes, journal),
                new ProfileScanner(app.Platform.FileSystem), new YakUI.Core.Install.InstallRegistry(app.Database), app.Platform.RhinoProcesses, journal, log);
            var r = await installer.InstallAsync(new YakUI.Core.Install.InstallRequest(target, plan.Name, plan.Version, file, YakUI.Core.Compat.DistributionTag.Parse(null)), ct);
            Console.WriteLine($"Установлено: {r.Name} {r.Version} → {r.VersionDirectory} ({r.Method})");
            return ExitOk;
        }

        var fullRoot = Path.GetFullPath(root);
        var prepared = plan.Kind == YakUI.Core.Install.ImportKind.Yak ? null : YakUI.Core.Install.FileImport.Prepare(plan, fullRoot);
        var cr = CustomInstallerFor(app, journal).Install(new YakUI.Core.Install.CustomInstallRequest(target, fullRoot, plan.Name, plan.Version,
            unpack ? YakUI.Core.Install.InstallMode.Unpack : YakUI.Core.Install.InstallMode.Custom,
            YakFile: plan.Kind == YakUI.Core.Install.ImportKind.Yak ? file : null, PreparedDirectory: prepared, Source: plan.Source, SourceFile: file));
        PrintCustom(cr);
        return ExitOk;
    }

    /// <summary>Удаление по пользовательскому пути по реестру (FR-46): <c>uninstall &lt;пакет&gt; --target N --root папка</c>.</summary>
    private static async Task<int> UninstallCustomAsync(string name, int major, string root, CancellationToken ct)
    {
        using var app = await Composition.CreateAsync(ct);
        if (TargetOrError(app, major) is not { } target)
            return ExitError;
        var r = CustomInstallerFor(app, new OperationJournal(app.Database)).Uninstall(target, name, Path.GetFullPath(root));
        Console.WriteLine($"Удалено: {r.Name} ({string.Join(", ", r.Versions)}); ссылок и объектов: {r.RemovedLinks.Count}");
        foreach (var l in r.RemovedLinks)
            Console.WriteLine($"  {l}");
        return ExitOk;
    }

    /// <summary>Удаление пакета из цели целиком (FR-48) через <see cref="YakUI.Core.Install.StandardUninstaller"/>.</summary>
    private static async Task<int> UninstallAsync(string[] args, CancellationToken ct)
    {
        int? major = null;
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--target" && i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var m))
            {
                major = m;
                i++;
            }
            else
                positional.Add(args[i]);
        }

        if (positional.Count != 1 || major is null)
            return Usage("Ожидается: uninstall <пакет> --target <major>");

        using var app = await Composition.CreateAsync(ct);
        var target = app.Targets.Where(t => t.Major == major).MaxBy(t => t.Release);
        if (target is null)
        {
            Console.Error.WriteLine($"Rhino {major} не установлен.");
            return ExitError;
        }

        var log = new FileLog(Path.Combine(app.Platform.Paths.DataDirectory, "logs"), FileLog.ParseLevel(app.Settings.LogLevel));
        var journal = new OperationJournal(app.Database);
        var uninstaller = new YakUI.Core.Install.StandardUninstaller(app.Library, new YakCli(app.Platform.Processes, journal),
            new ProfileScanner(app.Platform.FileSystem), new YakUI.Core.Install.InstallRegistry(app.Database), app.Platform.RhinoProcesses, journal, log);
        var preview = uninstaller.Preview(target, positional[0]);
        if (preview.BlockedReason is { } blocked)
        {
            Console.Error.WriteLine(blocked);
            return ExitError;
        }

        Console.WriteLine($"Удаляется {preview.Name} из {target.DisplayName}; версии: {string.Join(", ", preview.Versions)}");
        var r = await uninstaller.UninstallAsync(target, positional[0], ct);
        Console.WriteLine($"Удалено: {r.Name} ({(r.Method == YakUI.Core.Install.InstallMethod.Yak ? "yak" : "без yak, по реестру YakUI")})");
        if (r.PendingRestart)
            Console.WriteLine($"{target.DisplayName} запущен — изменения применятся после перезапуска.");
        return ExitOk;
    }

    /// <summary>Загрузка иконок каталога в кэш (FR-34, FR-34b): 4 потока; итог — источники, трафик, время.</summary>
    private static async Task<int> IconsAsync(string[] args, CancellationToken ct)
    {
        var limit = int.MaxValue;
        var recheck = args.Contains("--recheck");
        args = args.Where(a => a != "--recheck").ToArray();
        if (args.Length == 2 && args[0] == "--diagnose" && int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var d))
            return await DiagnoseIconsAsync(d, args.Length > 2 ? args[2..] : [], ct);
        if (args.Length == 2 && args[0] == "--limit" && int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var l))
            limit = l;
        else if (args.Length != 0)
            return Usage("Ожидается: icons [--limit N] [--recheck] | icons --diagnose N");

        var platform = new WindowsPlatform();
        var settings = new SettingsStore(platform.Paths.ConfigDirectory).Load();
        using var db = OpenDatabase(platform.Paths);
        using var api = new YakUI.Core.Api.YakApiClient(new YakUI.Core.Api.YakApiOptions { BaseUri = new Uri(settings.ApiBaseUrl!) });
        var snapshot = new YakUI.Core.Catalog.CatalogService(api, db, TimeSpan.FromMinutes(settings.CatalogIndexTtlMinutes)).LoadSnapshot();
        var log = new FileLog(Path.Combine(platform.Paths.DataDirectory, "logs"), FileLog.ParseLevel(settings.LogLevel));
        using var service = new YakUI.Core.Icons.IconService(Path.Combine(platform.Paths.DataDirectory, "icons"), Path.Combine(platform.Paths.DataDirectory, "downloads"), log: log, journal: new OperationJournal(db), scaler: platform.Images)
        {
            RecheckPlaceholders = recheck,
        };
        var loader = new YakUI.Core.Icons.IconLoader(service, 4, log);

        var requests = snapshot.Packages.Select(YakUI.Core.Icons.IconRequests.For).OfType<YakUI.Core.Icons.IconRequest>().Take(limit).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tasks = requests.Select(r => loader.Request(r, visible: false)).ToList();
        while (tasks.Any(t => !t.IsCompleted))
        {
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(5000, ct));
            Console.WriteLine($"  {tasks.Count(t => t.IsCompleted)}/{tasks.Count}, сеть {service.BytesDownloaded / 1024.0 / 1024:0.0} МБ");
        }

        await loader.StopAsync();
        var results = tasks.Select(t => t.Result).ToList();
        Console.WriteLine();
        Console.WriteLine($"Пакетов:                {results.Count}");
        foreach (var g in results.GroupBy(r => r.Source).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key,-14} {g.Count()}");
        Console.WriteLine($"Тип содержимого узнан:  {results.Count(r => r.ContentKind is not null)}");
        Console.WriteLine($"Трафик:                 {service.BytesDownloaded / 1024.0 / 1024:0.0} МБ");
        Console.WriteLine($"Время:                  {sw.Elapsed:mm\\:ss}");
        return ExitOk;
    }

    /// <summary>
    /// Диагностика «нет иконки»: N случайных пакетов с заглушкой (плюс названные через запятую в YAKUI_ICON_NAMES) —
    /// поле icon и icon_url манифеста последней версии, icon_url версий из API, картинки в архиве.
    /// </summary>
    private static async Task<int> DiagnoseIconsAsync(int count, string[] _, CancellationToken ct)
    {
        var platform = new WindowsPlatform();
        var settings = new SettingsStore(platform.Paths.ConfigDirectory).Load();
        using var db = OpenDatabase(platform.Paths);
        using var api = new YakUI.Core.Api.YakApiClient(new YakUI.Core.Api.YakApiOptions { BaseUri = new Uri(settings.ApiBaseUrl!) });
        var catalog = new YakUI.Core.Catalog.CatalogService(api, db, TimeSpan.FromMinutes(settings.CatalogIndexTtlMinutes));
        using var inspections = new YakUI.Core.Archive.YakInspectionService(db);
        using var icons = new YakUI.Core.Icons.IconService(Path.Combine(platform.Paths.DataDirectory, "icons"));
        var snapshot = catalog.LoadSnapshot();

        var named = (Environment.GetEnvironmentVariable("YAKUI_ICON_NAMES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var placeholders = snapshot.Packages
            .Select(p => (Package: p, Request: YakUI.Core.Icons.IconRequests.For(p)))
            .Where(x => x.Request is not null && icons.GetCached(x.Request) is { Source: YakUI.Core.Icons.IconSource.Placeholder })
            .ToList();
        var rnd = new Random(20261013);
        var picked = snapshot.Packages.Where(p => named.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).Select(p => (Package: p, Request: YakUI.Core.Icons.IconRequests.For(p)))
            .Concat(placeholders.OrderBy(_ => rnd.Next()).Take(count))
            .ToList();
        Console.WriteLine($"Пакетов с заглушкой: {placeholders.Count}; проверяю {picked.Count}");

        string[] imageExt = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg", ".webp"];
        foreach (var (p, r) in picked)
        {
            Console.WriteLine();
            Console.WriteLine($"== {p.Name} {r!.Version}  ({r.YakFilename})");
            var versions = (await catalog.GetVersionsAsync(p.Name, ct)).Value;
            var withUrl = versions.Where(v => v.IconUrl is not null).Select(v => v.Version).ToList();
            Console.WriteLine($"   icon_url в API: последняя — {(versions.FirstOrDefault(v => v.Version == r.Version)?.IconUrl ?? "нет")}; у версий: {(withUrl.Count == 0 ? "ни у одной" : string.Join(", ", withUrl.Take(5)))}");
            if (r.YakUrl is null)
                continue;
            try
            {
                var i = await inspections.InspectAsync(r.YakUrl, r.YakFilename, ct);
                Console.WriteLine($"   manifest: icon={i.Manifest?.Icon ?? "нет"}, icon_url={i.Manifest?.IconUrl ?? "нет"}{(i.ManifestError is null ? "" : ", ошибка: " + i.ManifestError)}");
                var images = i.Entries.Where(e => imageExt.Any(x => e.Path.EndsWith(x, StringComparison.OrdinalIgnoreCase))).ToList();
                Console.WriteLine($"   картинки в архиве ({images.Count}): {string.Join(", ", images.Take(8).Select(e => $"{e.Path} ({e.Size / 1024.0:0.#} КБ)"))}{(images.Count > 8 ? " …" : "")}");
            }
            catch (Exception e) when (e is HttpRequestException or InvalidDataException or YakUI.Core.Archive.RangeNotSupportedException)
            {
                Console.WriteLine($"   архив недоступен: {e.Message}");
            }
        }

        return ExitOk;
    }

    /// <summary>Инспекция .yak по HTTPS-ссылке (частичное чтение, FR-36b) или локального файла (FR-31).</summary>
    private static async Task<int> InspectAsync(string[] args, CancellationToken ct)
    {
        if (args.Length != 1)
            return Usage("Ожидается: inspect <https-ссылка | путь к .yak>");

        var target = args[0];
        YakUI.Core.Archive.YakInspection inspection;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var platform = new WindowsPlatform();
            using var db = OpenDatabase(platform.Paths);
            using var svc = new YakUI.Core.Archive.YakInspectionService(db);
            var cached = svc.GetCached(target) is not null;
            inspection = await svc.InspectAsync(target, Path.GetFileName(new Uri(target).LocalPath), ct);
            if (cached)
                Console.WriteLine("(из кэша инспекций)");
        }
        else
        {
            var source = new YakUI.Core.Archive.FileRangeSource(target);
            inspection = (await YakUI.Core.Archive.YakInspector.InspectAsync(source, Path.GetFileName(target), ct: ct)).Inspection;
        }

        sw.Stop();
        var m = inspection.Manifest;
        Console.WriteLine($"Файл:        {inspection.Filename}");
        Console.WriteLine($"Архив:       {inspection.ArchiveSize / 1024.0 / 1024:0.0} МБ, распаковано {inspection.UncompressedSize / 1024.0 / 1024:0.0} МБ, файлов {inspection.Entries.Count}{(inspection.IsZip64 ? ", ZIP64" : "")}");
        Console.WriteLine($"Прочитано:   {inspection.BytesRead / 1024.0:0.0} КБ{(inspection.ReadWholeFile ? " (целиком: Range не поддерживается)" : "")} за {sw.Elapsed.TotalMilliseconds:0} мс");
        Console.WriteLine($"Манифест:    {(m is null ? "нет — " + inspection.ManifestError : $"{m.Name} {m.Version}; авторы: {string.Join(", ", m.Authors)}; иконка: {m.Icon ?? "—"}")}");
        Console.WriteLine($"Тип:         {inspection.ContentKind}");
        Console.WriteLine($"Таргеты:     {string.Join(", ", inspection.Frameworks)}");
        Console.WriteLine($".rhp:        {string.Join(", ", inspection.RhinoPlugins.DefaultIfEmpty("—"))}");
        Console.WriteLine($".gha/.ghpy:  {string.Join(", ", inspection.GrasshopperAssemblies.Take(8).DefaultIfEmpty("—"))}{(inspection.GrasshopperAssemblies.Count > 8 ? $" … ещё {inspection.GrasshopperAssemblies.Count - 8}" : "")}");
        Console.WriteLine($".ghuser:     {(inspection.GrasshopperUserObjects.Count == 0 ? "—" : inspection.GrasshopperUserObjects.Count.ToString(CultureInfo.InvariantCulture))}");
        Console.WriteLine($".gh/.ghx:    {(inspection.GrasshopperDefinitions.Count == 0 ? "—" : inspection.GrasshopperDefinitions.Count.ToString(CultureInfo.InvariantCulture))}");
        if (inspection.UnsafePaths.Count > 0)
            Console.WriteLine($"ВНИМАНИЕ, опасные пути: {string.Join(", ", inspection.UnsafePaths)}");
        return ExitOk;
    }

    /// <summary>
    /// NFR-05a: при открытом окне YakUI CLI не мигрирует схему и не откладывает повреждённую базу —
    /// <see cref="DatabaseBusyException"/> перехватывается в Main и выводится понятным сообщением.
    /// </summary>
    private static YakUiDatabase OpenDatabase(IPlatformPaths paths)
    {
        var appRunning = SingleInstance.IsAppRunning(paths.DataDirectory);
        var db = YakUiDatabase.Open(paths.DatabasePath(), new DatabaseOpenOptions
        {
            AllowExclusive = !appRunning,
            OpenLogPath = Path.Combine(paths.DataDirectory, "logs", "db-open.log"), // 21.7
            Caller = "CLI " + string.Join(' ', Environment.GetCommandLineArgs().Skip(1)),
        });
        if (db.QuarantinedPath is not null)
            Console.Error.WriteLine($"Внимание: база повреждена ({db.QuarantineReason}); отложена в {db.QuarantinedPath}, создана новая.");
        return db;
    }

    /// <summary>IProgress без захвата контекста: вызывается сразу в потоке краулера.</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        private readonly object _lock = new();

        public void Report(T value)
        {
            lock (_lock)
                handler(value);
        }
    }

    private static void Print(InstalledReport r)
    {
        var t = r.Target;
        Console.WriteLine($"== {t.DisplayName}");
        Console.WriteLine($"   {t.InstallPath}");
        Console.WriteLine();

        Console.WriteLine($"Профиль пользователя: {t.UserPackagesDirectory}");
        PrintPackages(r.UserPackages, r.Discrepancies);

        Console.WriteLine();
        Console.WriteLine($"Для всех пользователей (только чтение): {t.AllUsersPackagesDirectory}");
        PrintPackages(r.AllUsersPackages, []);

        Console.WriteLine();
        if (r.Yak is not null)
            Console.WriteLine($"yak list: {r.Yak.Version} ({r.Yak.Path}) → …\\{r.YakList?.PackageDirectorySuffix ?? "?"}, пакетов: {r.YakList?.Packages.Count.ToString(CultureInfo.InvariantCulture) ?? "—"}");
        if (r.YakListProblem is not null)
            Console.WriteLine($"yak list недоступен: {r.YakListProblem}");

        var missingInProfile = r.Discrepancies.Where(d => d.Kind == DiscrepancyKind.MissingInProfile).ToList();
        foreach (var d in missingInProfile)
            Console.WriteLine($"  ! {d.Name} {d.YakVersion}: есть в yak list, нет в профиле");

        if (r.YakList is not null && r.Discrepancies.Count == 0)
            Console.WriteLine("  Расхождений между профилем и yak list нет.");

        Console.WriteLine();
    }

    private static void PrintPackages(IReadOnlyList<ProfilePackage> packages, IReadOnlyList<Discrepancy> discrepancies)
    {
        if (packages.Count == 0)
        {
            Console.WriteLine("  (пусто)");
            return;
        }

        var byName = discrepancies.ToLookup(d => d.Name, StringComparer.OrdinalIgnoreCase);
        var width = Math.Min(40, packages.Max(p => p.Name.Length));
        foreach (var p in packages)
        {
            var line = new StringBuilder($"  {p.Name.PadRight(width)}  {p.ActiveVersion ?? "?"}");
            var inactive = p.InactiveVersions.ToList();
            if (inactive.Count > 0)
                line.Append($"  [неактивные: {string.Join(", ", inactive)}]");
            if (p.ManifestTxtMissing)
                line.Append("  [нет manifest.txt]");
            foreach (var d in byName[p.Name])
            {
                line.Append(d.Kind switch
                {
                    DiscrepancyKind.MissingInYakList => "  ! нет в yak list",
                    DiscrepancyKind.VersionMismatch => $"  ! yak list: {d.YakVersion}",
                    _ => "",
                });
            }

            Console.WriteLine(line.ToString());
        }
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        PrintUsage();
        return ExitUsage;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            YakUI.Cli — консольный режим YakUI

            Команды:
              env [--out <файл>]       отчёт об окружении: Rhino, среды .NET, профили, yak по версиям Rhino (FR-06)
              list [--target <major>]  установленные пакеты по целям: профиль пользователя, профиль «для всех»
                                       (только чтение) и yak list yak-а цели с расхождениями (FR-55)
              catalog [--rebuild]      построить / обновить кэш каталога (FR-20); --rebuild — с нуля
              download <пакет> [версия] [--all] [--target <major>]
                                       скачать .yak в папку загрузок (FR-36, FR-37); --all — все дистрибутивы версии
              install <пакет> [версия] --target <major> [--builtin]
                                       скачать (или взять из загрузок) лучший дистрибутив и установить в профиль
                                       текущего пользователя: yak цели, при отказе — встроенный установщик (AD-03, FR-50);
                                       --builtin — сразу встроенный установщик
              update <пакет> [версия] --target <major>
                                       сменить версию: обновить до самой новой подходящей или откатить на указанную
                                       (FR-49: новая ставится, затем прежняя снимается; при ошибке остаётся прежняя)
              update-all --target <major> [--only a,b]
                                       обновить все пакеты цели, кроме закреплённых (FR-59)
              pin|unpin <пакет> --target <major>
                                       закрепить активную версию / снять закрепление (FR-58)
              yak-check                новые сборки yak на files.mcneel.com по версиям Rhino (FR-12, 6.2)
              yak-download <версия>    скачать и проверить yak.exe (FR-10, FR-14, FR-17)
              install <пакет> [версия] --target <major> --root <папка> [--unpack] [--runtime netfx|netcore]
                                       пользовательский путь: <папка>\<имя>\<версия>, Grasshopper — .ghlink на подпапку
                                       среды цели (FR-42, FR-43); --unpack — только распаковать (FR-45)
              import <файл> --target <major> [--root <папка> [--unpack]]
                                       импорт .yak / .zip / .gha / .ghuser / .rhp / .rhi (FR-101…FR-103)
              uninstall <пакет> --target <major> --root <папка>
                                       удалить установку по пользовательскому пути по реестру YakUI (FR-46)
              uninstall <пакет> --target <major>
                                       удалить пакет из профиля текущего пользователя целиком, все версии (FR-48):
                                       yak цели, без yak — только поставленное встроенным установщиком YakUI
              icons [--limit N] [--recheck]  загрузить иконки каталога в кэш (FR-34); --recheck — перепроверить заглушки
              icons --diagnose N       N случайных пакетов без иконки: поле icon, icon_url версий, картинки в архиве
              search [--target 8|7.14] [--query текст] [--all] [--prerelease] [--platform win|mac|all] [--sort name|downloads|month|updated|created] [--names]
                                       каталог с фильтрами экрана «Каталог» (FR-22…FR-27); без --target — любая версия

            Коды возврата: 0 — успех, 1 — ошибка, 2 — неверные аргументы.
            """);
    }

    /// <summary>Сборка зависимостей для одной команды.</summary>
    private sealed class Composition : IDisposable
    {
        private Composition(WindowsPlatform platform, AppSettings settings, YakUiDatabase db, IReadOnlyList<RhinoTarget> targets, YakToolLibrary library)
        {
            Platform = platform;
            Settings = settings;
            Database = db;
            Targets = targets;
            Library = library;
        }

        public WindowsPlatform Platform { get; }

        public AppSettings Settings { get; }

        public YakUiDatabase Database { get; }

        public IReadOnlyList<RhinoTarget> Targets { get; }

        public YakToolLibrary Library { get; }

        public static async Task<Composition> CreateAsync(CancellationToken ct)
        {
            var platform = new WindowsPlatform();
            var settings = new SettingsStore(platform.Paths.ConfigDirectory).Load();
            var db = OpenDatabase(platform.Paths);
            try
            {
                var journal = new OperationJournal(db);
                var targets = new RhinoDiscovery(platform.Registry, platform.FileSystem, platform.Paths, new DotNetRuntimeTable(settings.DotNetRuntimes!), settings.ReleaseTypeRules).Discover();
                var library = new YakToolLibrary(platform.FileSystem, platform.Paths, platform.Signatures, platform.DotNet, new YakCli(platform.Processes, journal), settings)
                {
                    TrustedDownload = new YakDownloadRegistry(db).IsTrusted, // FR-17
                };
                await library.RefreshAsync(targets, ct);
                return new Composition(platform, settings, db, targets, library);
            }
            catch
            {
                db.Dispose();
                throw;
            }
        }

        public void Dispose() => Database.Dispose();
    }
}
