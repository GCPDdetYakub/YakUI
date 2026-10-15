using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using YakUI.Core.Archive;
using YakUI.Core.Platform;
using YakUI.Core.Localization;

namespace YakUI.Core.Install;

/// <summary>Формат импортируемого файла (таблица 13a).</summary>
public enum ImportKind
{
    Yak,
    Zip,
    GhAssembly,
    UserObject,
    RhinoPlugin,
    RhinoInstaller,

    /// <summary>Папка пакета целиком (перетаскиванием или «Добавить папку…»).</summary>
    Folder,
    Unsupported,
}

/// <summary>Что получится из файла: имя, версия, источник (FR-101, FR-102).</summary>
public sealed record ImportPlan(string File, ImportKind Kind, string Name, string Version, InstallSource Source, string? Problem)
{
    /// <summary>Одиночный .gha/.rhp: найденные рядом и недостающие сборки (13a).</summary>
    public DependencyReport Dependencies { get; init; } = DependencyReport.None;

    /// <summary>Подпапки сред с сборками Grasshopper (net48, net7.0…): несколько — рекомендуется стандартная установка (FR-43).</summary>
    public IReadOnlyList<string> RuntimeFolders { get; init; } = [];

    public bool CanInstall => Problem is null && Kind is not (ImportKind.Unsupported or ImportKind.RhinoInstaller);

    /// <summary>Стандартный профиль доступен только для <c>.yak</c> (остальные форматы — в свою папку, 13a).</summary>
    public bool StandardAllowed => Kind == ImportKind.Yak;
}

/// <summary>
/// Импорт скачанного файла (FR-101…FR-103, таблица 13a): распознавание формата, имени и версии, источника
/// («Импорт: Food4Rhino» — по адресу в Mark-of-the-Web) и подготовка папки версии для <see cref="CustomInstaller"/>.
/// <c>.msi</c>/<c>.exe</c> — FR-104, версия 1.1: не поддерживаются.
/// </summary>
public static partial class FileImport
{
    public static ImportPlan Plan(string file, IFileSystem fs, IMarkOfTheWeb motw, TimeProvider? time = null)
    {
        if (Directory.Exists(file))
            return PlanFolder(file, time);
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var kind = ext switch
        {
            ".yak" => ImportKind.Yak,
            ".zip" => ImportKind.Zip,
            ".gha" or ".ghpy" => ImportKind.GhAssembly,
            ".ghuser" => ImportKind.UserObject,
            ".rhp" => ImportKind.RhinoPlugin,
            ".rhi" => ImportKind.RhinoInstaller,
            _ => ImportKind.Unsupported,
        };
        var source = SourceOf(motw.Read(file));
        var baseName = SafeName(Path.GetFileNameWithoutExtension(file));
        var date = (time ?? TimeProvider.System).GetLocalNow().ToString("yyyy.M.d", CultureInfo.InvariantCulture);

        if (!File.Exists(file))
            return new ImportPlan(file, kind, baseName, date, source, L.T("FileImport.77", $"Файл не найден."));

        switch (kind)
        {
            case ImportKind.Unsupported:
                return new ImportPlan(file, kind, baseName, date, source,
                    ext is ".msi" or ".exe" ? L.T("FileImport.76", $"Сторонние установщики (.msi, .exe) — в версии 1.1 (FR-104).") : L.T("FileImport.75", $"Формат {ext} не поддерживается."));
            case ImportKind.Yak:
                try
                {
                    using var zip = ZipFile.OpenRead(file);
                    var entry = zip.GetEntry("manifest.yml") ?? throw new InvalidDataException(L.T("FileImport.74", $"В архиве нет manifest.yml."));
                    using var reader = new StreamReader(entry.Open());
                    var m = YakManifest.Parse(reader.ReadToEnd().TrimStart('﻿'));
                    var folders = RuntimeFoldersOf(zip.Entries.Select(e => e.FullName));
                    return m.Name is { } n && m.Version is { } v
                        ? new ImportPlan(file, kind, n, v, source, null) { RuntimeFolders = folders }
                        : new ImportPlan(file, kind, baseName, date, source, L.T("FileImport.73", $"В manifest.yml нет name или version."));
                }
                catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException)
                {
                    return new ImportPlan(file, kind, baseName, date, source, e.Message);
                }

            case ImportKind.Zip:
                try
                {
                    using var zip = ZipFile.OpenRead(file);
                    var useful = zip.Entries.Count(e => Path.GetExtension(e.FullName).ToLowerInvariant() is ".gha" or ".ghpy" or ".rhp" or ".ghuser");
                    var (name, version) = NameAndVersion(Path.GetFileNameWithoutExtension(file), date);
                    return new ImportPlan(file, kind, name, version, source,
                        useful == 0 ? L.T("FileImport.72", $"В архиве нет .gha, .ghpy, .rhp или .ghuser.") : null)
                    {
                        RuntimeFolders = RuntimeFoldersOf(zip.Entries.Select(e => e.FullName)),
                    };
                }
                catch (Exception e) when (e is InvalidDataException or IOException)
                {
                    return new ImportPlan(file, kind, baseName, date, source, e.Message);
                }

            default:
                var fileVersion = fs.GetFileVersion(file);
                var ver = fileVersion is { } fv && fv > new Version(0, 0)
                    ? fv.ToString()
                    : NameAndVersion(Path.GetFileNameWithoutExtension(file), date).Version;
                // Одиночный .gha/.rhp: какие сборки ему нужны и есть ли они рядом (13a, только метаданные).
                var deps = kind is ImportKind.GhAssembly or ImportKind.RhinoPlugin ? AssemblyDependencies.Analyze(file) : DependencyReport.None;
                return new ImportPlan(file, kind, NameAndVersion(Path.GetFileNameWithoutExtension(file), date).Name, ver, source, null) { Dependencies = deps };
        }
    }

    /// <summary>Папка пакета целиком: имя и версия — по имени папки; нужны .gha/.ghpy/.rhp/.ghuser внутри.</summary>
    private static ImportPlan PlanFolder(string dir, TimeProvider? time)
    {
        var date = (time ?? TimeProvider.System).GetLocalNow().ToString("yyyy.M.d", CultureInfo.InvariantCulture);
        var (name, version) = NameAndVersion(Path.GetFileName(dir.TrimEnd('\\', '/')), date);
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
        var useful = files.Count(f => Path.GetExtension(f).ToLowerInvariant() is ".gha" or ".ghpy" or ".rhp" or ".ghuser");
        return new ImportPlan(dir, ImportKind.Folder, name, version, InstallSource.File,
            useful == 0 ? L.T("FileImport.71", $"В папке нет .gha, .ghpy, .rhp или .ghuser.") : files.Count > 100_000 ? L.T("FileImport.70", $"Слишком много файлов в папке.") : null)
        {
            RuntimeFolders = RuntimeFoldersOf(files.Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))),
        };
    }

    /// <summary>Подпапки сред (net48, net7.0…), в которых есть сборки Grasshopper.</summary>
    public static IReadOnlyList<string> RuntimeFoldersOf(IEnumerable<string> relativePaths) =>
        relativePaths
            .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".gha" or ".ghpy")
            .Select(p => p.Replace('\\', '/').Split('/'))
            .Where(parts => parts.Length >= 2)
            .Select(parts => parts[^2])
            .Where(f => GhLinks.ParseFramework(f) is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Папка версии во временном месте рядом с корнем: для <c>.zip</c> — распакованное содержимое (защита от zip-slip и
    /// лимиты, NFR-04; одна общая папка верхнего уровня снимается), для одиночного файла — копия файла.
    /// </summary>
    public static string Prepare(ImportPlan plan, string root, ExtractLimits? limits = null, bool includeDependencies = true)
    {
        Directory.CreateDirectory(root);
        var temp = Path.Combine(root, ".yakui-tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (plan.Kind == ImportKind.Zip)
            {
                BuiltInInstaller.Extract(plan.File, temp, limits ?? new ExtractLimits());
                // Архив с единственной папкой верхнего уровня — содержимое этой папки.
                var top = Directory.GetFileSystemEntries(temp);
                if (top.Length == 1 && Directory.Exists(top[0]))
                {
                    var inner = temp + "-inner";
                    Directory.Move(top[0], inner);
                    Directory.Delete(temp);
                    Directory.Move(inner, temp);
                }
            }
            else if (plan.Kind == ImportKind.Folder)
            {
                CopyTree(plan.File, temp);
            }
            else
            {
                Directory.CreateDirectory(temp);
                File.Copy(plan.File, Path.Combine(temp, Path.GetFileName(plan.File)));
                // 13a: найденные рядом зависимости — вместе с файлом (по решению пользователя).
                if (includeDependencies)
                {
                    foreach (var dep in plan.Dependencies.Found)
                        File.Copy(dep, Path.Combine(temp, Path.GetFileName(dep)), overwrite: true);
                }
            }

            return temp;
        }
        catch
        {
            BuiltInInstaller.DeleteQuietly(temp);
            throw;
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    /// <summary>Food4Rhino — если в Zone.Identifier (HostUrl/ReferrerUrl) есть food4rhino.com.</summary>
    public static InstallSource SourceOf(string? zone) =>
        zone is not null && zone.Contains("food4rhino.com", StringComparison.OrdinalIgnoreCase) ? InstallSource.Food4Rhino : InstallSource.File;

    /// <summary>«Bowerbird_2.8.0» → (Bowerbird, 2.8.0); без номера — дата.</summary>
    public static (string Name, string Version) NameAndVersion(string stem, string fallbackVersion)
    {
        var m = VersionInName().Match(stem);
        if (m.Success)
        {
            var name = SafeName(stem[..m.Index].TrimEnd(' ', '_', '-', '.', 'v', 'V'));
            return (name.Length == 0 ? SafeName(stem) : name, m.Groups["v"].Value);
        }

        return (SafeName(stem), fallbackVersion);
    }

    private static string SafeName(string s)
    {
        var cleaned = new string(s.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "Package" : cleaned;
    }

    [GeneratedRegex(@"[ _\-v.]*(?<v>\d+(?:\.\d+){1,3})(?:[^\d].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionInName();
}
