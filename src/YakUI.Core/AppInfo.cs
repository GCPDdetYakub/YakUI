using System.Reflection;

namespace YakUI.Core;

public static class AppInfo
{
    /// <summary>Версия YakUI для <c>User-Agent: YakUI/&lt;ver&gt;</c> (NFR-03).</summary>
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];
}
