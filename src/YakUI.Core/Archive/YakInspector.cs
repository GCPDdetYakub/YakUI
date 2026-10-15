using System.Text;
using System.Text.RegularExpressions;
using YakUI.Core.Localization;

namespace YakUI.Core.Archive;

/// <summary>Тип содержимого пакета (FR-24; данные собираются уже сейчас, фильтр — позже).</summary>
public enum PackageContentKind
{
    None,
    Rhino,
    Grasshopper,
    Mixed,
}

public sealed record YakArchiveEntry(string Path, long Size, long CompressedSize);

/// <summary>Результат инспекции <c>.yak</c> (FR-31). Сериализуется в кэш.</summary>
public sealed record YakInspection
{
    public string? Filename { get; init; }
    public long ArchiveSize { get; init; }
    public long UncompressedSize { get; init; }
    public bool IsZip64 { get; init; }
    public IReadOnlyList<YakArchiveEntry> Entries { get; init; } = [];

    /// <summary>Rhino-плагины <c>.rhp</c>.</summary>
    public IReadOnlyList<string> RhinoPlugins { get; init; } = [];

    /// <summary>Grasshopper-сборки <c>.gha</c> / <c>.ghpy</c>.</summary>
    public IReadOnlyList<string> GrasshopperAssemblies { get; init; } = [];

    /// <summary>Пользовательские объекты Grasshopper <c>.ghuser</c> (FR-31).</summary>
    public IReadOnlyList<string> GrasshopperUserObjects { get; init; } = [];

    /// <summary>Определения Grasshopper <c>.gh</c> / <c>.ghx</c> (FR-31).</summary>
    public IReadOnlyList<string> GrasshopperDefinitions { get; init; } = [];

    /// <summary>Версия формата результата; кэш старее <see cref="YakInspector.FormatVersion"/> перечитывается.</summary>
    public int FormatVersion { get; init; }

    /// <summary>Таргеты .NET по подпапкам (<c>net48</c>, <c>net7.0</c>, …); <c>root</c> — сборки в корне без подпапки.</summary>
    public IReadOnlyList<string> Frameworks { get; init; } = [];

    public PackageContentKind ContentKind { get; init; }

    public YakManifest? Manifest { get; init; }

    public string? ManifestError { get; init; }

    /// <summary>Пути с <c>..</c>, абсолютные или с диском — признак zip-slip (NFR-04).</summary>
    public IReadOnlyList<string> UnsafePaths { get; init; } = [];

    /// <summary>Сколько байтов пришлось прочитать (частичное чтение, FR-36b).</summary>
    public long BytesRead { get; init; }

    public bool ReadWholeFile { get; init; }
}

/// <summary>
/// Инспекция <c>.yak</c> без загрузки всего архива (FR-31, FR-36b): оглавление ZIP с конца файла,
/// затем только <c>manifest.yml</c>. Иконка читается отдельно (шаг 2.6) через <see cref="ZipDirectory.ReadEntryAsync"/>.
/// </summary>
public static partial class YakInspector
{
    /// <summary>2 — учёт <c>.ghuser</c>, <c>.gh</c>, <c>.ghx</c> (FR-31, ТЗ 0.3.2).</summary>
    public const int FormatVersion = 2;

    public static async Task<(YakInspection Inspection, ZipDirectory Directory)> InspectAsync(IRangeSource source, string? filename = null, ZipLimits? limits = null, CancellationToken ct = default)
    {
        var dir = await ZipDirectory.ReadAsync(source, limits, ct).ConfigureAwait(false);
        var files = dir.Entries.Where(e => !e.IsDirectory).ToList();

        YakManifest? manifest = null;
        string? manifestError = null;
        var manifestEntry = dir.Find("manifest.yml");
        if (manifestEntry is null)
        {
            manifestError = L.T("YakInspector.17", $"В архиве нет manifest.yml.");
        }
        else
        {
            try
            {
                var bytes = await dir.ReadEntryAsync(manifestEntry, ct).ConfigureAwait(false);
                manifest = YakManifest.Parse(Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException)
            {
                manifestError = e.Message;
            }
        }

        var rhp = files.Where(f => HasExt(f.Name, ".rhp")).Select(f => f.Name).ToList();
        var gha = files.Where(f => HasExt(f.Name, ".gha") || HasExt(f.Name, ".ghpy")).Select(f => f.Name).ToList();
        var ghuser = files.Where(f => HasExt(f.Name, ".ghuser")).Select(f => f.Name).ToList();
        var ghDefs = files.Where(f => HasExt(f.Name, ".gh") || HasExt(f.Name, ".ghx")).Select(f => f.Name).ToList();

        // Определения .gh/.ghx — примеры, а не код: тип содержимого по ним не определяется.
        var kind = (rhp.Count > 0, gha.Count > 0 || ghuser.Count > 0) switch
        {
            (true, true) => PackageContentKind.Mixed,
            (true, false) => PackageContentKind.Rhino,
            (false, true) => PackageContentKind.Grasshopper,
            _ => PackageContentKind.None,
        };

        return (new YakInspection
        {
            Filename = filename,
            ArchiveSize = dir.ArchiveSize,
            UncompressedSize = files.Sum(f => f.UncompressedSize),
            IsZip64 = dir.IsZip64,
            Entries = files.Select(f => new YakArchiveEntry(f.Name, f.UncompressedSize, f.CompressedSize)).ToList(),
            RhinoPlugins = rhp,
            GrasshopperAssemblies = gha,
            GrasshopperUserObjects = ghuser,
            GrasshopperDefinitions = ghDefs,
            FormatVersion = FormatVersion,
            Frameworks = DetectFrameworks(files.Select(f => f.Name)),
            ContentKind = kind,
            Manifest = manifest,
            ManifestError = manifestError,
            UnsafePaths = dir.Entries.Where(e => IsUnsafe(e.Name)).Select(e => e.Name).ToList(),
            BytesRead = source.BytesTransferred,
            ReadWholeFile = source is HttpRangeSource { FullDownload: true },
        }, dir);
    }

    /// <summary>
    /// Таргеты .NET: первая папка пути вида <c>net48</c>, <c>net7.0</c>, <c>net8.0-windows</c>, <c>netstandard2.0</c>.
    /// Сборки плагинов в корне архива дают <c>root</c>.
    /// </summary>
    internal static IReadOnlyList<string> DetectFrameworks(IEnumerable<string> paths)
    {
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            var parts = p.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var tfm = parts.Take(parts.Length - 1).FirstOrDefault(s => TfmRegex().IsMatch(s));
            if (tfm is not null)
                result.Add(tfm.ToLowerInvariant());
            else if (parts.Length == 1 && (HasExt(p, ".rhp") || HasExt(p, ".gha") || HasExt(p, ".dll")))
                result.Add("root");
        }

        return result.ToList();
    }

    internal static bool IsUnsafe(string path)
    {
        var p = path.Replace('\\', '/');
        return p.StartsWith('/') || p.Contains(':') || p.Split('/').Any(s => s == "..");
    }

    private static bool HasExt(string path, string ext) => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^net(\d+(\.\d+)?|standard\d+(\.\d+)?|coreapp\d+(\.\d+)?)(-[a-z]+[\d.]*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex TfmRegex();
}
