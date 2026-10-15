using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using YakUI.App.Resources;
using YakUI.Core.Catalog;
using YakUI.Core.Compat;

namespace YakUI.App.ViewModels;

/// <summary>Строка каталога (FR-22, FR-30 — кратко).</summary>
public sealed class CatalogItemViewModel
{
    public CatalogItemViewModel(CatalogEntry entry, PackageIconViewModel? icon = null)
    {
        Entry = entry;
        Icon = icon ?? new PackageIconViewModel(entry.Package.Name, null, null);
        var p = entry.Package;

        Name = p.Name;
        Authors = p.Authors ?? "";
        Description = FirstLine(p.Description);
        Version = entry.TargetVersion ?? "";
        IsPrerelease = entry.TargetVersionIsPrerelease;
        Downloads = string.Format(CultureInfo.CurrentCulture, Strings.Cat_Downloads, p.DownloadCount.ToString("N0", CultureInfo.CurrentCulture));
        IsPending = entry.IsPending;
        IsMacOnly = p.IsMacOnly;
        InstalledText = entry.InstalledVersion is { } iv ? string.Format(CultureInfo.CurrentCulture, Strings.Cat_Installed, iv) : null;
        UpdateText = entry.HasUpdate ? string.Format(CultureInfo.CurrentCulture, Strings.Cat_UpdateTo, entry.TargetVersion) : null;

        StatusText = entry.Status is { } s ? StatusName(s) : IsPending ? Strings.Status_Pending : null;
        IsNative = entry.Status == CompatibilityStatus.Native;
        IsBackward = entry.Status == CompatibilityStatus.BackwardCompatible;
        IsUnrestricted = entry.Status == CompatibilityStatus.Unrestricted;
        IsIncompatible = entry.Status == CompatibilityStatus.Incompatible;
        IsUnknown = entry.Status == CompatibilityStatus.Unknown;

        // FR-34a (до шага 2.6): первая буква имени на цветном фоне, цвет — из хэша имени.
        Initial = Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";
        InitialBrush = new ImmutableSolidColorBrush(ColorFromName(Name)); // создаётся в фоновом потоке
    }

    public CatalogEntry Entry { get; }

    /// <summary>Иконка пакета (FR-34); пока не загружена — заглушка FR-34a.</summary>
    public PackageIconViewModel Icon { get; }

    public string Name { get; }

    public string Authors { get; }

    public string? Description { get; }

    public string Version { get; }

    public bool IsPrerelease { get; }

    public string Downloads { get; }

    public string? StatusText { get; }

    public bool IsNative { get; }

    public bool IsBackward { get; }

    public bool IsUnrestricted { get; }

    public bool IsIncompatible { get; }

    public bool IsUnknown { get; }

    public bool IsPending { get; }

    public bool IsMacOnly { get; }

    public string? InstalledText { get; }

    public string? UpdateText { get; }

    public string Initial { get; }

    public IBrush InitialBrush { get; }

    public static string StatusName(CompatibilityStatus s) => s switch
    {
        CompatibilityStatus.Native => Strings.Status_Native,
        CompatibilityStatus.BackwardCompatible => Strings.Status_Backward,
        CompatibilityStatus.Unrestricted => Strings.Status_Unrestricted,
        CompatibilityStatus.Incompatible => Strings.Status_Incompatible,
        _ => Strings.Status_Unknown,
    };

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var line = text.Trim().Split('\n', 2)[0].Trim();
        return line.Length > 220 ? line[..220] + "…" : line;
    }

    /// <summary>Стабильный цвет по имени (FNV-1a), приглушённая палитра, читаемая с белой буквой в обеих темах.</summary>
    internal static Color ColorFromName(string name)
    {
        uint h = 2166136261;
        foreach (var ch in name.ToLowerInvariant())
            h = (h ^ ch) * 16777619;
        var hue = h % 360;
        return HslToColor(hue, 0.45, 0.42);
    }

    private static Color HslToColor(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
