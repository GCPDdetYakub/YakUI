using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using YakUI.Core.Compat;
using YakUI.Core.Localization;

namespace YakUI.Core.Install;

/// <summary>
/// Ссылки Grasshopper <c>&lt;Имя&gt;.ghlink</c> (FR-43, 21.6). Grasshopper берёт <c>.gha</c> только из папки ссылки и подпапки
/// не просматривает, поэтому для пакетов с подпапками сред (<c>net48</c>, <c>net7.0</c>, <c>net8.0-windows</c>…) ссылка ведёт
/// на подпапку под среду .NET цели; среда цели — по таблице 17a из настроек (<see cref="TargetRuntime"/>), в коде
/// конкретные версии не зашиты. Папка <c>Grasshopper\Libraries</c> общая для всех версий Rhino.
/// </summary>
public static partial class GhLinks
{
    public const string Extension = ".ghlink";

    private static readonly string[] GhFiles = [".gha", ".ghpy"];

    /// <summary>Среда, под которую собрана подпапка: <c>net48</c> → Framework 4.8; <c>net7.0</c>, <c>net8.0-windows</c> → .NET 7, 8.</summary>
    public static (DotNetRuntimeKind Kind, Version Version)? ParseFramework(string folder)
    {
        var m = FrameworkName().Match(folder);
        if (!m.Success)
            return null;
        if (m.Groups["fx"].Success)
        {
            var digits = m.Groups["fx"].Value; // 48, 472, 462
            var v = new Version(digits[0] - '0', digits.Length > 1 ? int.Parse(digits[1..], CultureInfo.InvariantCulture) : 0);
            return (DotNetRuntimeKind.NetFramework, v);
        }

        return (DotNetRuntimeKind.NetCore, Version.Parse(m.Groups["core"].Value));
    }

    /// <summary>
    /// Подпапка под среду цели: для .NET Framework — самая новая <c>net4x</c>; для .NET — самая новая <c>netN.x</c> с N не выше
    /// версии среды цели (без <c>-windows</c> предпочтительнее при равных). null — подходящей нет или среда цели неизвестна.
    /// </summary>
    public static string? SelectRuntimeFolder(IEnumerable<string> folders, TargetRuntime runtime)
    {
        if (runtime.Runtime is not { } kind || runtime.Version is null)
            return null;
        var target = Version.TryParse(runtime.Version.Contains('.', StringComparison.Ordinal) ? runtime.Version : runtime.Version + ".0", out var tv) ? tv : null;
        if (target is null)
            return null;

        return folders
            .Select(f => (Folder: f, Fw: ParseFramework(f)))
            .Where(x => x.Fw is { } fw && fw.Kind == kind && (kind == DotNetRuntimeKind.NetFramework || fw.Version.Major <= target.Major))
            .OrderByDescending(x => x.Fw!.Value.Version)
            .ThenBy(x => x.Folder.Contains('-', StringComparison.Ordinal) ? 1 : 0)
            .Select(x => x.Folder)
            .FirstOrDefault();
    }

    /// <summary>
    /// Папка для ссылки: папка версии, если <c>.gha</c>/<c>.ghpy</c> лежат в её корне; иначе подпапка среды цели;
    /// null — сборок Grasshopper нет или нет подпапки под среду цели.
    /// </summary>
    public static string? LinkFolder(string versionDirectory, TargetRuntime runtime)
    {
        if (HasGh(versionDirectory))
            return versionDirectory;
        var sub = Directory.EnumerateDirectories(versionDirectory).Where(HasGh).Select(Path.GetFileName).OfType<string>().ToList();
        return SelectRuntimeFolder(sub, runtime) is { } chosen ? Path.Combine(versionDirectory, chosen) : null;
    }

    /// <summary>Есть ли сборки Grasshopper где-либо в папке версии (с подпапками).</summary>
    public static bool HasAnyGh(string versionDirectory) =>
        Directory.Exists(versionDirectory)
        && Directory.EnumerateFiles(versionDirectory, "*", SearchOption.AllDirectories).Any(f => GhFiles.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));

    private static bool HasGh(string dir) =>
        Directory.EnumerateFiles(dir).Any(f => GhFiles.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));

    public static string LinkPath(string librariesDirectory, string packageName) => Path.Combine(librariesDirectory, packageName + Extension);

    /// <summary>Папка, на которую указывает ссылка (первая непустая строка).</summary>
    public static string? ReadTarget(string linkPath) =>
        File.Exists(linkPath)
            ? File.ReadAllLines(linkPath).Select(l => l.Trim().TrimStart('﻿')).FirstOrDefault(l => l.Length > 0)
            : null;

    /// <summary>
    /// Создать или обновить ссылку атомарно (временный файл → перенос). Чужую ссылку с тем же именем (не созданную
    /// YakUI, <paramref name="ours"/> = false) не перезаписывает — <see cref="InstallFailedException"/>.
    /// </summary>
    public static string Write(string librariesDirectory, string packageName, string folder, bool ours)
    {
        Directory.CreateDirectory(librariesDirectory);
        var path = LinkPath(librariesDirectory, packageName);
        if (File.Exists(path) && !ours && !string.Equals(ReadTarget(path)?.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InstallFailedException(L.T("GhLinks.78", $"В {librariesDirectory} уже есть {Path.GetFileName(path)} (указывает на {ReadTarget(path)}), созданный не YakUI."));
        File.WriteAllText(path + ".tmp", folder, new UTF8Encoding(false));
        File.Move(path + ".tmp", path, overwrite: true);
        return path;
    }

    /// <summary>Удалить ссылку, если она по-прежнему указывает в папку пакета (удаляется только созданное YakUI).</summary>
    public static bool Remove(string linkPath, string packageDirectory)
    {
        if (ReadTarget(linkPath) is not { } target)
            return false;
        var root = Path.GetFullPath(packageDirectory).TrimEnd('\\') + "\\";
        if (!(Path.GetFullPath(target).TrimEnd('\\') + "\\").StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;
        File.Delete(linkPath);
        return true;
    }

    [GeneratedRegex(@"^net(?:(?<fx>4\d{1,2})|(?<core>\d+\.\d+))(?:-[a-z]+(?:[\d.]+)?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex FrameworkName();
}
