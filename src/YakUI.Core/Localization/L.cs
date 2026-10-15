using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace YakUI.Core.Localization;

/// <summary>
/// Сообщения ядра на языке интерфейса (FR-82). Русский текст — в коде (язык по умолчанию), переводы —
/// в <c>Localization/messages_&lt;язык&gt;.json</c> (ключ → формат с <c>{0}</c>, <c>{1}</c>…). Нет перевода — английский, для русского интерфейса — текст из кода.
/// Журналы и диагностические логи не переводятся.
/// </summary>
public static class L
{
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Tables = new(StringComparer.Ordinal);

    /// <summary>Сообщение с числами и датами в формате текущей культуры.</summary>
    public static string T(string key, FormattableString text) => Format(key, text, CultureInfo.CurrentCulture);

    /// <summary>Сообщение с числами в инвариантном формате (байты, коды, диапазоны).</summary>
    public static string Ti(string key, FormattableString text) => Format(key, text, CultureInfo.InvariantCulture);

    private static string Format(string key, FormattableString text, CultureInfo formatCulture)
    {
        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        // Нет таблицы языка (например, DE до 1.1) — английский: окно в этом случае тоже на английском.
        if (lang != "ru" && (Table(lang).TryGetValue(key, out var translated) || Table("en").TryGetValue(key, out translated)))
            return string.Format(formatCulture, translated, text.GetArguments());
        return text.ToString(formatCulture);
    }

    /// <summary>Таблица перевода языка; нет файла — пустая.</summary>
    internal static IReadOnlyDictionary<string, string> Table(string lang) => Tables.GetOrAdd(lang, static l =>
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream($"YakUI.Core.Localization.messages_{l}.json");
        return stream is null
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
    });
}
