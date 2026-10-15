using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using YakUI.App.Resources;

namespace YakUI.App.ViewModels;

public enum ScreenKind
{
    Catalog,
    Installed,
    Downloads,
    Sources,
    Profiles,
    Journal,
    Settings,
}

/// <summary>Экран главного окна (FR-80).</summary>
public abstract class ScreenViewModel : ObservableObject
{
    protected ScreenViewModel(ScreenKind kind, string title)
    {
        Kind = kind;
        Title = title;
    }

    public ScreenKind Kind { get; }

    public string Title { get; }
}

/// <summary>Заглушка экрана, который появится на следующих шагах или этапах.</summary>
public sealed class PlaceholderScreenViewModel : ScreenViewModel
{
    public PlaceholderScreenViewModel(ScreenKind kind, string title, string message, TargetSelectorViewModel? target = null)
        : base(kind, title)
    {
        Message = message;
        Target = target;
    }

    public string Message { get; }

    /// <summary>Если задан — экран показывает выбранную цель (проверка шага 2.2).</summary>
    public TargetSelectorViewModel? Target { get; }

    public bool ShowsTarget => Target is not null;

    public static string Step(string step) => string.Format(CultureInfo.CurrentCulture, Strings.Placeholder_Step, step);

    public static string Stage(int stage) => string.Format(CultureInfo.CurrentCulture, Strings.Placeholder_Stage, stage);
}
