using System.Globalization;
using System.Net;
using YakUI.Core.Localization;

namespace YakUI.Core.Net;

/// <summary>Режим прокси (FR-95).</summary>
public enum ProxyMode
{
    /// <summary>Прокси Windows и переменные окружения — как это делает .NET.</summary>
    System,
    None,
    Manual,
}

public enum ProxyKind
{
    Http,
    Socks5,
}

/// <summary>Настройки сети (FR-95): логин и пароль прокси в 1.0 не поддерживаются.</summary>
public sealed record ProxySettings
{
    public ProxyMode Mode { get; init; }

    public ProxyKind Kind { get; init; }

    public string? Host { get; init; }

    public int Port { get; init; }

    /// <summary><c>http://host:port</c> или <c>socks5://host:port</c>; null — не «Вручную» или адрес неполный.</summary>
    public Uri? ManualUri =>
        Mode == ProxyMode.Manual && !string.IsNullOrWhiteSpace(Host) && Port is > 0 and <= 65535
        && Uri.TryCreate(string.Create(CultureInfo.InvariantCulture, $"{(Kind == ProxyKind.Socks5 ? "socks5" : "http")}://{HostForUri(Host)}:{Port}"), UriKind.Absolute, out var u)
            ? u
            : null;

    private static string HostForUri(string host)
    {
        host = host.Trim().Trim('[', ']');
        return host.Contains(':') ? "[" + host + "]" : host; // IPv6
    }

    /// <summary>Проверка ввода «Вручную»; текст ошибки или null.</summary>
    public string? Validate()
    {
        if (Mode != ProxyMode.Manual)
            return null;
        if (string.IsNullOrWhiteSpace(Host))
            return L.T("NetworkProxy.127", $"Укажите адрес прокси.");
        if (Host.Contains("://", StringComparison.Ordinal) || Host.Contains('@') || Host.Contains('/'))
            return L.T("NetworkProxy.126", $"Адрес — только имя хоста или IP, без схемы, логина и пути.");
        if (Port is <= 0 or > 65535)
            return L.T("NetworkProxy.125", $"Порт — число от 1 до 65535.");
        return ManualUri is null ? L.T("NetworkProxy.124", $"Неверный адрес прокси.") : null;
    }

    /// <summary>Разбор «host:port» или «socks5://host:port» из одного поля ввода.</summary>
    public static ProxySettings? ParseAddress(string text, ProxyKind defaultKind)
    {
        text = text.Trim();
        var kind = defaultKind;
        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            var scheme = text[..schemeEnd].ToLowerInvariant();
            kind = scheme switch
            {
                "socks5" or "socks5h" => ProxyKind.Socks5,
                "http" => ProxyKind.Http,
                _ => (ProxyKind)(-1),
            };
            if ((int)kind < 0)
                return null;
            text = text[(schemeEnd + 3)..].TrimEnd('/');
        }

        var colon = text.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            return null;
        var host = text[..colon].Trim('[', ']');
        return new ProxySettings { Mode = ProxyMode.Manual, Kind = kind, Host = host, Port = port };
    }

    /// <summary>
    /// Переменные окружения для yak.exe (FR-95a): «Вручную» — ALL_PROXY/HTTPS_PROXY/HTTP_PROXY и NO_PROXY;
    /// «Без прокси» — переменные очищаются; «Системный» — ничего не меняется (yak унаследует окружение).
    /// </summary>
    public IReadOnlyDictionary<string, string?> YakEnvironment()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        switch (Mode)
        {
            case ProxyMode.Manual when ManualUri is { } u:
                var value = u.GetLeftPart(UriPartial.Authority);
                env["ALL_PROXY"] = value;
                env["HTTPS_PROXY"] = value;
                env["HTTP_PROXY"] = value;
                env["NO_PROXY"] = "localhost,127.0.0.1";
                break;
            case ProxyMode.None:
                foreach (var name in new[] { "ALL_PROXY", "HTTPS_PROXY", "HTTP_PROXY" })
                    env[name] = null;
                env["NO_PROXY"] = "*";
                break;
        }

        return env;
    }
}

/// <summary>
/// Прокси для всех HTTP-запросов YakUI (FR-95): один экземпляр на процесс, режим меняется без перезапуска —
/// пулы соединений .NET разделены по адресу прокси, новые запросы идут по новому режиму.
/// </summary>
public sealed class SwitchableProxy : IWebProxy
{
    private volatile ProxySettings _settings = new();

    /// <summary>Общий экземпляр для обработчиков по умолчанию (API, .yak, иконки, HEAD).</summary>
    public static SwitchableProxy Shared { get; } = new();

    public ProxySettings Settings
    {
        get => _settings;
        set => _settings = value;
    }

    public ICredentials? Credentials { get; set; }

    public Uri? GetProxy(Uri destination) => _settings switch
    {
        { Mode: ProxyMode.Manual } s => s.ManualUri,
        { Mode: ProxyMode.None } => null,
        _ => HttpClient.DefaultProxy.IsBypassed(destination) ? null : HttpClient.DefaultProxy.GetProxy(destination),
    };

    public bool IsBypassed(Uri host) => _settings switch
    {
        { Mode: ProxyMode.Manual } s => s.ManualUri is null || host.IsLoopback,
        { Mode: ProxyMode.None } => true,
        _ => HttpClient.DefaultProxy.IsBypassed(host),
    };

    /// <summary>Обработчик по умолчанию для клиентов YakUI: системные таймауты задают сами клиенты.</summary>
    public static SocketsHttpHandler CreateHandler(DecompressionMethods decompression = DecompressionMethods.None) => new()
    {
        Proxy = Shared,
        UseProxy = true,
        AutomaticDecompression = decompression,
    };
}
