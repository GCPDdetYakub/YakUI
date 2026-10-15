using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using YakUI.App.Resources;
using YakUI.Core.Catalog;

namespace YakUI.App.ViewModels;

/// <summary>Индикатор кэша каталога в заголовке: построение, готовность, офлайн (FR-21, FR-29).</summary>
public sealed partial class CatalogStatusViewModel : ObservableObject
{
    [ObservableProperty]
    private string _text = Strings.Catalog_StatusEmpty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isOffline;

    public void ShowProgress(CrawlProgress p)
    {
        IsBusy = p.Done < p.Total;
        Text = string.Format(CultureInfo.CurrentCulture, Strings.Catalog_StatusBuilding, p.Done, p.Total);
    }

    public void ShowReady(int packages, int pending, DateTimeOffset fetchedAt, bool offline, DateTimeOffset now)
    {
        IsBusy = false;
        IsOffline = offline;
        var when = FormatAge(fetchedAt, now);
        Text = offline
            ? string.Format(CultureInfo.CurrentCulture, Strings.Catalog_StatusOffline, when)
            : pending > 0
                ? string.Format(CultureInfo.CurrentCulture, Strings.Catalog_StatusPending, packages, pending)
                : string.Format(CultureInfo.CurrentCulture, Strings.Catalog_StatusReady, packages, when);
    }

    /// <summary>«только что», «12 мин назад», «3 ч назад», иначе дата.</summary>
    internal static string FormatAge(DateTimeOffset at, DateTimeOffset now)
    {
        var age = now - at;
        if (age < TimeSpan.FromMinutes(1))
            return Strings.Age_JustNow;
        if (age < TimeSpan.FromHours(1))
            return string.Format(CultureInfo.CurrentCulture, Strings.Age_Minutes, (int)age.TotalMinutes);
        if (age < TimeSpan.FromDays(1))
            return string.Format(CultureInfo.CurrentCulture, Strings.Age_Hours, (int)age.TotalHours);
        return at.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
    }
}
