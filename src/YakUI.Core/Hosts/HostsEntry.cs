using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using YakUI.Core.Localization;

namespace YakUI.Core.Hosts;

/// <summary>Контрольная строка hosts «IP имя_хоста» (FR-89h).</summary>
public sealed partial record HostsEntry(IPAddress Ip, string Host)
{
    [GeneratedRegex(@"^(?=.{1,253}$)[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex HostName();

    /// <summary>Разбор «IP имя»; при ошибке — понятное сообщение.</summary>
    public static bool TryParse(string? text, out HostsEntry? entry, out string? error)
    {
        entry = null;
        var parts = (text ?? "").Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            error = L.T("HostsEntry.Format", $"Формат строки — «IP имя_хоста», например «127.0.0.1 example.com».");
            return false;
        }

        if (!TryParseIp(parts[0], out var ip))
        {
            error = L.T("HostsEntry.BadIp", $"«{parts[0]}» — не адрес IPv4 или IPv6.");
            return false;
        }

        if (!HostName().IsMatch(parts[1]))
        {
            error = L.T("HostsEntry.BadHost", $"«{parts[1]}» — недопустимое имя хоста.");
            return false;
        }

        entry = new HostsEntry(ip, parts[1].ToLowerInvariant());
        error = null;
        return true;
    }

    /// <summary>IPv4 — только полная запись из четырёх чисел (IPAddress.TryParse принимает и «127.1»); IPv6 — любая.</summary>
    internal static bool TryParseIp(string text, out IPAddress ip)
    {
        if (!IPAddress.TryParse(text, out ip!))
            return false;
        return ip.AddressFamily == AddressFamily.InterNetworkV6 || text.Count(c => c == '.') == 3;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Ip} {Host}");

    public bool Matches(IPAddress ip, string host) => Ip.Equals(ip) && string.Equals(Host, host, StringComparison.OrdinalIgnoreCase);
}
