using System.ComponentModel;
using System.Diagnostics;
using YakUI.Core.Platform;

namespace YakUI.Platform.Windows;

/// <summary>
/// Запущенные Rhino цели (FR-05): процессы <c>Rhino.exe</c> из папки установки и хосты Rhino.Inside
/// (Revit и др.), в которые загружен <c>RhinoCore.dll</c> этой установки. Чужие процессы без доступа пропускаются.
/// </summary>
public sealed class WindowsRhinoProcessProbe : IRhinoProcessProbe
{
    /// <summary>Хосты, в которых проверяются модули (перебор модулей всех процессов был бы слишком дорогим).</summary>
    public static readonly string[] InsideHosts = ["Revit", "acad", "ArchiCAD", "Unity", "TeklaStructures", "Bentley.Connect"];

    public IReadOnlyList<RunningRhino> FindRunning(string installPath)
    {
        var root = Path.GetFullPath(installPath).TrimEnd('\\') + "\\";
        var core = Path.Combine(root, "System", "RhinoCore.dll");
        var result = new List<RunningRhino>();

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.ProcessName.Equals("Rhino", StringComparison.OrdinalIgnoreCase))
                    {
                        if (p.MainModule?.FileName is { } exe && exe.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            result.Add(new RunningRhino(p.Id, p.ProcessName, p.StartTime.ToUniversalTime(), false));
                    }
                    else if (InsideHosts.Any(h => p.ProcessName.StartsWith(h, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (p.Modules.Cast<ProcessModule>().Any(m => string.Equals(m.FileName, core, StringComparison.OrdinalIgnoreCase)))
                            result.Add(new RunningRhino(p.Id, p.ProcessName, p.StartTime.ToUniversalTime(), true));
                    }
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // нет доступа к процессу или он уже завершился
                }
            }
        }

        return result;
    }
}

/// <summary>Открывает папку в Проводнике (FR-58).</summary>
public sealed class WindowsFolderOpener : IFolderOpener
{
    public bool Open(string path)
    {
        if (!Directory.Exists(path))
            return false;
        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        psi.ArgumentList.Add(Path.GetFullPath(path));
        using var _ = Process.Start(psi);
        return true;
    }

    public bool Reveal(string filePath)
    {
        if (!File.Exists(filePath))
            return Open(Path.GetDirectoryName(filePath) ?? filePath);
        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        psi.ArgumentList.Add("/select," + Path.GetFullPath(filePath));
        using var _ = Process.Start(psi);
        return true;
    }
}
