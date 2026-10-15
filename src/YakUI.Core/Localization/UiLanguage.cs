using System.Globalization;
using YakUI.Core.Settings;

namespace YakUI.Core.Localization;

/// <summary>Язык интерфейса (FR-82): русский и английский в 1.0; «Как в системе» — русский для русской Windows, иначе английский.</summary>
public static class UiLanguage
{
    /// <summary>Культура интерфейса для настройки; <paramref name="system"/> — язык Windows (по умолчанию текущий).</summary>
    public static CultureInfo Resolve(AppLanguage language, CultureInfo? system = null) => language switch
    {
        AppLanguage.Russian => CultureInfo.GetCultureInfo("ru-RU"),
        AppLanguage.English => CultureInfo.GetCultureInfo("en-US"),
        _ => (system ?? CultureInfo.InstalledUICulture).TwoLetterISOLanguageName == "ru"
            ? CultureInfo.GetCultureInfo("ru-RU")
            : CultureInfo.GetCultureInfo("en-US"),
    };

    /// <summary>Применить до создания окон: строки интерфейса читаются при запуске, смена — после перезапуска.</summary>
    public static void Apply(AppLanguage language)
    {
        var culture = Resolve(language);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
