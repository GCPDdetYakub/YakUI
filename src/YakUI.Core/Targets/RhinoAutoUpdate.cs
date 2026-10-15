using System.Xml;
using YakUI.Core.Platform;

namespace YakUI.Core.Targets;

/// <summary>Галочка «Automatically update packages when Rhino starts» (ОВ-19, 21.5).</summary>
public enum AutoUpdateState
{
    /// <summary>У этой версии Rhino галочки нет (Rhino 7 и старше).</summary>
    NotSupported,
    Enabled,
    Disabled,
}

/// <summary>
/// Чтение (только чтение) галочки автообновления пакетов Rhino (ОВ-19, 21.5):
/// <c>%APPDATA%\McNeel\Rhinoceros\&lt;N.0&gt;\settings\settings-Scheme__Default.xml</c>,
/// <c>Options/PackageManager/CheckForUpdates</c>. Узла нет — значение по умолчанию: включена.
/// Галочка есть с Rhino 8; у будущих версий считается, что она тоже есть.
/// </summary>
public sealed class RhinoAutoUpdateReader
{
    public const int FirstMajor = 8;

    private readonly IFileSystem _fs;
    private readonly IPlatformPaths _paths;

    public RhinoAutoUpdateReader(IFileSystem fs, IPlatformPaths paths)
    {
        _fs = fs;
        _paths = paths;
    }

    public string SettingsFile(int major) =>
        Path.Combine(Path.GetDirectoryName(_paths.RhinoUserPackagesRoot.TrimEnd('\\', '/'))!, $"{major}.0", "settings", "settings-Scheme__Default.xml");

    public AutoUpdateState Read(int major)
    {
        if (major < FirstMajor)
            return AutoUpdateState.NotSupported;

        var file = SettingsFile(major);
        if (!_fs.FileExists(file))
            return AutoUpdateState.Enabled;

        try
        {
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(_fs.ReadAllText(file));
            var node = doc.SelectSingleNode("/settings/settings/child[@key='Options']/child[@key='PackageManager']/entry[@key='CheckForUpdates']")
                       ?? doc.SelectSingleNode("//child[@key='PackageManager']/entry[@key='CheckForUpdates']");
            return node is not null && bool.TryParse(node.InnerText.Trim(), out var on) && !on
                ? AutoUpdateState.Disabled
                : AutoUpdateState.Enabled;
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException)
        {
            // Нечитаемый файл — безопасное допущение: Rhino обновляет пакеты (ОВ-19).
            return AutoUpdateState.Enabled;
        }
    }
}
