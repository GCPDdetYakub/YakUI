using System.IO.Compression;
using System.Text;
using YakUI.Core.Archive;
using YakUI.Core.Localization;

namespace YakUI.Core.Install;

public sealed class InstallFailedException : Exception
{
    public InstallFailedException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Ограничения распаковки (NFR-04).</summary>
public sealed record ExtractLimits
{
    public int MaxEntries { get; init; } = 100_000;

    /// <summary>Сумма распакованных размеров; LunchBox 2025 — около 1 ГБ.</summary>
    public long MaxTotalBytes { get; init; } = 4L * 1024 * 1024 * 1024;
}

public sealed record BuiltInResult(string Name, string Version, string VersionDirectory, bool ReplacedExisting);

/// <summary>
/// Встроенный установщик в стандартный профиль (FR-50, FR-42): распаковка <c>.yak</c> во временную папку рядом
/// с профилем (тот же том — атомарный move), защита от zip-slip и «бомб» (NFR-04), проверка <c>manifest.yml</c>,
/// перенос как есть в <c>&lt;профиль&gt;\&lt;Имя&gt;\&lt;Версия&gt;\</c> (подпапки net48/net7.0 сохраняются),
/// затем атомарная запись <c>&lt;Имя&gt;\manifest.txt</c> с активной версией одной строкой с переводом строки LF, как пишет yak.
/// Уже существующая папка той же версии заменяется только после успешной распаковки, при сбое — возвращается.
/// Другие версии пакета не трогаются (смена версии с удалением старой — FR-49, этап 4).
/// </summary>
public static class BuiltInInstaller
{
    private const string TempPrefix = ".yakui-tmp-";

    public static BuiltInResult Install(string yakFile, string profileDirectory, ExtractLimits? limits = null, string? expectedName = null, string? expectedVersion = null)
    {
        limits ??= new ExtractLimits();
        Directory.CreateDirectory(profileDirectory);
        CleanupLeftovers(profileDirectory);
        var temp = Path.Combine(profileDirectory, TempPrefix + Guid.NewGuid().ToString("N"));
        string? versionDir = null;   // целевая папка версии
        string? backup = null;       // куда отодвинута прежняя папка той же версии
        var movedNew = false;        // новая папка уже на месте
        string? createdPackageDir = null;
        try
        {
            Extract(yakFile, temp, limits);

            var manifestPath = Path.Combine(temp, "manifest.yml");
            if (!File.Exists(manifestPath))
                throw new InstallFailedException(L.T("BuiltInInstaller.57", $"В архиве нет manifest.yml."));
            var manifest = YakManifest.Parse(File.ReadAllText(manifestPath).TrimStart('\uFEFF'));
            var name = manifest.Name ?? throw new InstallFailedException(L.T("BuiltInInstaller.56", $"В manifest.yml нет поля name."));
            var version = manifest.Version ?? throw new InstallFailedException(L.T("BuiltInInstaller.55", $"В manifest.yml нет поля version."));
            if (expectedName is not null && !name.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                throw new InstallFailedException(L.T("BuiltInInstaller.54", $"Архив содержит пакет «{name}», ожидался «{expectedName}»."));
            if (expectedVersion is not null && !SameVersion(version, expectedVersion))
                throw new InstallFailedException(L.T("BuiltInInstaller.53", $"Архив содержит версию {version}, ожидалась {expectedVersion}."));
            if (!IsSafeSegment(name) || !IsSafeSegment(version))
                throw new InstallFailedException(L.T("BuiltInInstaller.52", $"Недопустимое имя или версия в manifest.yml: «{name}» «{version}»."));

            var packageDir = Path.Combine(profileDirectory, name);
            versionDir = Path.Combine(packageDir, version);
            if (!Directory.Exists(packageDir))
            {
                Directory.CreateDirectory(packageDir);
                createdPackageDir = packageDir;
            }

            var replaced = Directory.Exists(versionDir);
            if (replaced)
            {
                backup = Path.Combine(profileDirectory, TempPrefix + "old-" + Guid.NewGuid().ToString("N"));
                Directory.Move(versionDir, backup);
            }

            Directory.Move(temp, versionDir);
            movedNew = true;

            var manifestTxt = Path.Combine(packageDir, "manifest.txt");
            File.WriteAllText(manifestTxt + ".tmp", version + "\n", new UTF8Encoding(false)); // как yak 0.13/0.15: версия и LF
            File.Move(manifestTxt + ".tmp", manifestTxt, overwrite: true);

            if (backup is not null)
                DeleteQuietly(backup);
            return new BuiltInResult(name, version, versionDir, replaced);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Rollback();
            throw new InstallFailedException(L.T("BuiltInInstaller.51", $"Встроенный установщик: {e.Message}"), e);
        }
        catch
        {
            Rollback();
            throw;
        }

        // При любом сбое профиль возвращается к прежнему состоянию (NFR-05).
        void Rollback()
        {
            DeleteQuietly(temp);
            if (movedNew && versionDir is not null)
                DeleteQuietly(versionDir);
            if (backup is not null && versionDir is not null && Directory.Exists(backup) && !Directory.Exists(versionDir))
                Directory.Move(backup, versionDir);
            if (createdPackageDir is not null && Directory.Exists(createdPackageDir) && !Directory.EnumerateFileSystemEntries(createdPackageDir).Any())
                Directory.Delete(createdPackageDir);
        }
    }

    /// <summary>Распаковка с защитой от zip-slip и лимитами; размер записи сверяется по факту, а не по заголовку.</summary>
    internal static void Extract(string yakFile, string destination, ExtractLimits limits)
    {
        using var zip = ZipFile.OpenRead(yakFile);
        if (zip.Entries.Count > limits.MaxEntries)
            throw new InstallFailedException(L.T("BuiltInInstaller.50", $"Слишком много файлов в архиве: {zip.Entries.Count}."));

        var root = Path.GetFullPath(destination).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        long total = 0;
        var buffer = new byte[1 << 16];
        foreach (var entry in zip.Entries)
        {
            if (YakInspector.IsUnsafe(entry.FullName))
                throw new InstallFailedException(L.T("BuiltInInstaller.49", $"Опасный путь в архиве: {entry.FullName}"));
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InstallFailedException(L.T("BuiltInInstaller.48", $"Путь выходит за папку распаковки: {entry.FullName}"));

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            long written = 0;
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                written += read;
                total += read;
                if (written > entry.Length || total > limits.MaxTotalBytes)
                    throw new InstallFailedException(L.T("BuiltInInstaller.47", $"Распакованный размер превышает допустимый ({entry.FullName})."));
                output.Write(buffer, 0, read);
            }
        }
    }

    /// <summary>Остатки прерванных установок (сбой процесса) старше часа.</summary>
    internal static void CleanupLeftovers(string profileDirectory)
    {
        if (!Directory.Exists(profileDirectory))
            return;
        foreach (var dir in Directory.EnumerateDirectories(profileDirectory, ".yakui-*")
                     .Where(d => System.IO.Path.GetFileName(d).StartsWith(TempPrefix, StringComparison.OrdinalIgnoreCase)
                              || System.IO.Path.GetFileName(d).StartsWith(StandardUninstaller.DeletePrefix, StringComparison.OrdinalIgnoreCase)))
        {
            if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > TimeSpan.FromHours(1))
                DeleteQuietly(dir);
        }
    }

    /// <summary>Одна и та же версия: строки или SemVer с учётом build-метаданных.</summary>
    public static bool SameVersion(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || (Versioning.PackageVersion.TryParse(a, out var x) && Versioning.PackageVersion.TryParse(b, out var y) && x == y && x.Build == y.Build);

    private static bool IsSafeSegment(string s) =>
        s.Length > 0 && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && s is not "." and not ".." && !s.EndsWith('.');

    internal static void DeleteQuietly(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // временная папка удалится при следующей установке или вручную — данные пакета не затронуты
        }
    }
}
