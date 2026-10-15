using System.Diagnostics;
using YakUI.Core.Platform;

namespace YakUI.Platform.Windows;

/// <summary>Открывает http(s)-ссылку в браузере по умолчанию (FR-33).</summary>
public sealed class WindowsUrlLauncher : IUrlLauncher
{
    public void Open(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException("Открываются только ссылки http/https.", nameof(url));
        using var _ = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    }
}
