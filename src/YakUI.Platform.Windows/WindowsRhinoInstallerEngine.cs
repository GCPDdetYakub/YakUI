using System.Diagnostics;
using Microsoft.Win32;
using YakUI.Core.Platform;

namespace YakUI.Platform.Windows;

/// <summary>
/// rhiexec.exe по ассоциации <c>.rhi</c> (21.2: <c>.rhi</c> → <c>Rhino.InstallerPackage</c> → shell\open\command;
/// на машине разработчика — <c>C:\Program Files\McNeel\Rhino Installer Engine\x64\rhiexec.exe</c>).
/// </summary>
public sealed class WindowsRhinoInstallerEngine : IRhinoInstallerEngine
{
    public string? Find()
    {
        try
        {
            using var ext = Registry.ClassesRoot.OpenSubKey(".rhi");
            if (ext?.GetValue(null) is not string progId)
                return null;
            using var cmd = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command");
            if (cmd?.GetValue(null) is not string command)
                return null;
            var exe = command.StartsWith('"') ? command[1..command.IndexOf('"', 1)] : command.Split(' ')[0];
            return File.Exists(exe) ? exe : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Launch(string rhiFile)
    {
        if (Find() is not { } exe)
            return false;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add(rhiFile);
        return Process.Start(psi) is not null;
    }
}
