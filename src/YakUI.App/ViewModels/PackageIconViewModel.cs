using System.Collections.Concurrent;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using YakUI.App.Services;
using YakUI.Core.Archive;
using YakUI.Core.Install;
using YakUI.Core.Catalog;
using YakUI.Core.Icons;

namespace YakUI.App.ViewModels;

/// <summary>Иконки пакетов для списка и карточки (FR-34…FR-34b).</summary>
public interface IIconProvider
{
    PackageIconViewModel For(CatalogPackage package);

    /// <summary>Иконка установленного пакета (FR-34: локальный манифест → каталог → заглушка).</summary>
    PackageIconViewModel ForInstalled(InstalledIconRequest request);

    /// <summary>Фоновая загрузка иконок всех пакетов снимка (FR-34b: «остальные в фоне»).</summary>
    void PrefetchAll(CatalogSnapshot snapshot);
}

/// <summary>
/// Иконка пакета. Пока картинки нет — заглушка FR-34a: первая буква на цветном фоне и малый значок типа
/// (R / GH / R+GH), если тип известен. Картинка запрашивается лениво — при первом обращении к <see cref="Image"/>,
/// то есть когда строку списка впервые рисуют (FR-34b).
/// </summary>
public sealed partial class PackageIconViewModel : ObservableObject
{
    private readonly Action<PackageIconViewModel>? _requestLoad;
    private int _requested;
    private Bitmap? _image;

    public PackageIconViewModel(string name, IconRequest? request, Action<PackageIconViewModel>? requestLoad)
    {
        Name = name;
        Request = request;
        _requestLoad = requestLoad;
        Initial = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?";
        Background = new ImmutableSolidColorBrush(CatalogItemViewModel.ColorFromName(name));
    }

    public string Name { get; }

    public IconRequest? Request { get; }

    public string Initial { get; }

    public IBrush Background { get; }

    /// <summary>Картинка; первое обращение ставит загрузку в приоритетную очередь.</summary>
    public Bitmap? Image
    {
        get
        {
            if (Interlocked.Exchange(ref _requested, 1) == 0)
                _requestLoad?.Invoke(this);
            return _image;
        }
    }

    public bool HasImage => _image is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKindBadge))]
    private string? _kindBadge;

    public bool HasKindBadge => KindBadge is not null && !HasImage;

    internal void SetResult(Bitmap? image, PackageContentKind? kind)
    {
        _image = image;
        KindBadge = kind switch
        {
            PackageContentKind.Rhino => "R",
            PackageContentKind.Grasshopper => "GH",
            PackageContentKind.Mixed => "R+GH",
            _ => null,
        };
        OnPropertyChanged(nameof(Image));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(HasKindBadge));
    }
}

/// <summary>Без загрузки — только заглушки (тесты, отсутствие сети).</summary>
public sealed class PlaceholderIconProvider : IIconProvider
{
    public PackageIconViewModel For(CatalogPackage package) => new(package.Name, null, null);

    public PackageIconViewModel ForInstalled(InstalledIconRequest request) => new(request.Name, null, null);

    public void PrefetchAll(CatalogSnapshot snapshot)
    {
    }
}

/// <summary>
/// Иконки через <see cref="IconLoader"/>: видимые строки — приоритетно, остальные — в фоне (4 потока).
/// Модель иконки одна на пакет и версию и переиспользуется при пересборке списка; картинка уменьшается до 96 px.
/// </summary>
public sealed class IconProvider : IIconProvider
{
    private const int DecodeWidth = 96;

    private readonly IconLoader _loader;
    private readonly IconService? _service;
    private readonly IUiDispatcher _ui;
    private readonly ConcurrentDictionary<string, PackageIconViewModel> _models = new(StringComparer.OrdinalIgnoreCase);

    public IconProvider(IconLoader loader, IUiDispatcher ui, IconService? service = null)
    {
        _loader = loader;
        _ui = ui;
        _service = service;
    }

    public PackageIconViewModel ForInstalled(InstalledIconRequest request)
    {
        var key = "installed\n" + request.Name + "\n" + request.Version;
        return _models.GetOrAdd(key, k => new PackageIconViewModel(request.Name, request.Catalog, model =>
        {
            if (_service is null)
                return;
            _ = Task.Run(async () =>
            {
                try
                {
                    Apply(model, await _service.GetInstalledAsync(request).ConfigureAwait(false));
                }
                catch (Exception)
                {
                    // локальная иконка не прочиталась — остаётся заглушка
                }
            });
        }));
    }

    public PackageIconViewModel For(CatalogPackage package)
    {
        var request = IconRequests.For(package);
        var key = package.Name + "\n" + (request?.Version ?? "");
        return _models.GetOrAdd(key, _ =>
        {
            var model = new PackageIconViewModel(package.Name, request, m => Load(m, visible: true));
            if (request is not null && _loader.TryGetCached(request) is { } cached)
                Apply(model, cached);
            return model;
        });
    }

    public void PrefetchAll(CatalogSnapshot snapshot)
    {
        foreach (var p in snapshot.Packages)
        {
            if (IconRequests.For(p) is { } r && _loader.TryGetCached(r) is null)
                _ = _loader.Request(r, visible: false);
        }
    }

    private void Load(PackageIconViewModel model, bool visible)
    {
        if (model.Request is not { } request || model.HasImage)
            return;

        _ = _loader.Request(request, visible).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
                Apply(model, t.Result);
        }, TaskScheduler.Default);
    }

    private void Apply(PackageIconViewModel model, IconResult result)
    {
        Bitmap? bitmap = null;
        if (result.FilePath is { } path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                bitmap = Bitmap.DecodeToWidth(stream, DecodeWidth, BitmapInterpolationMode.HighQuality);
            }
            catch (Exception)
            {
                // Файл прошёл проверку FR-34c, но декодер его не принял (или кэш повреждён) — показываем заглушку.
                bitmap = null;
            }
        }

        _ui.Post(() => model.SetResult(bitmap, result.ContentKind));
    }
}
