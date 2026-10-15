using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using YakUI.Core.Platform;

namespace YakUI.Platform.Windows;

public sealed class WindowsFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IEnumerable<string> EnumerateDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    public string ReadAllText(string path) => File.ReadAllText(path);

    public FileVersionStrings? GetVersionStrings(string path)
    {
        if (!File.Exists(path))
            return null;
        var info = FileVersionInfo.GetVersionInfo(path);
        return new FileVersionStrings(info.FileDescription, info.SpecialBuild, info.ProductName);
    }

    public Version? GetFileVersion(string path)
    {
        if (!File.Exists(path))
            return null;

        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.FileMajorPart != 0 || info.FileMinorPart != 0 || info.FileBuildPart != 0 || info.FilePrivatePart != 0)
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);

        return Version.TryParse(info.FileVersion?.Split(' ')[0], out var v) ? v : null;
    }

    public IEnumerable<string> EnumerateFilesRecursive(string path)
    {
        if (!Directory.Exists(path))
            return [];
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return Directory.EnumerateFiles(path, "*", options);
    }

    public DateTimeOffset? GetCreationTime(string path) =>
        Directory.Exists(path) ? Directory.GetCreationTimeUtc(path)
        : File.Exists(path) ? File.GetCreationTimeUtc(path)
        : null;

    public string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLower(CultureInfo.InvariantCulture);
    }
}
