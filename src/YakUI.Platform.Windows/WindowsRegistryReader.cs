using System.Globalization;
using Microsoft.Win32;
using YakUI.Core.Platform;

namespace YakUI.Platform.Windows;

/// <summary>Чтение реестра с явным выбором представления (64- или 32-битного).</summary>
public sealed class WindowsRegistryReader : IRegistryReader
{
    public IReadOnlyList<string> GetSubKeyNames(RegistryRoot root, string path, RegistryViewKind view)
    {
        using var key = Open(root, path, view);
        return key?.GetSubKeyNames() ?? [];
    }

    public IReadOnlyList<string> GetValueNames(RegistryRoot root, string path, RegistryViewKind view)
    {
        using var key = Open(root, path, view);
        return key?.GetValueNames() ?? [];
    }

    public string? GetString(RegistryRoot root, string path, string name, RegistryViewKind view)
    {
        using var key = Open(root, path, view);
        return key?.GetValue(name) switch
        {
            null => null,
            string s => s,
            var o => Convert.ToString(o, CultureInfo.InvariantCulture),
        };
    }

    private static RegistryKey? Open(RegistryRoot root, string path, RegistryViewKind view)
    {
        var hive = root == RegistryRoot.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
        var rv = view == RegistryViewKind.Registry32 ? RegistryView.Registry32 : RegistryView.Registry64;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, rv);
            return baseKey.OpenSubKey(path, writable: false);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
