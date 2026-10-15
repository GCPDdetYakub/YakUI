using YakUI.Core.Platform;

namespace YakUI.Platform.Windows;

/// <summary>
/// Пути YakUI и Rhino на Windows (16.1). При <c>portable.flag</c> рядом с exe все данные YakUI — в <c>.\data\</c>.
/// </summary>
public sealed class WindowsPlatformPaths : IPlatformPaths
{
    public const string PortableFlagName = "portable.flag";

    public WindowsPlatformPaths(string? appDirectory = null)
    {
        var appDir = appDirectory ?? AppContext.BaseDirectory;
        IsPortable = File.Exists(Path.Combine(appDir, PortableFlagName));

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        DataDirectory = IsPortable ? Path.Combine(appDir, "data") : Path.Combine(localAppData, "YakUI");
        ConfigDirectory = IsPortable ? Path.Combine(DataDirectory, "config") : Path.Combine(appData, "YakUI");

        RhinoUserPackagesRoot = Path.Combine(appData, "McNeel", "Rhinoceros", "packages");
        RhinoProgramDataRoot = Path.Combine(programData, "McNeel", "Rhinoceros");
        GrasshopperLibraries = Path.Combine(appData, "Grasshopper", "Libraries");

        ProgramFilesDirectories = new[]
            {
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            }
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string ConfigDirectory { get; }

    public string DataDirectory { get; }

    public bool IsPortable { get; }

    public string RhinoUserPackagesRoot { get; }

    public string RhinoProgramDataRoot { get; }

    public string GrasshopperLibraries { get; }

    public IReadOnlyList<string> ProgramFilesDirectories { get; }
}
