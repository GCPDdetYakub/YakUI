using System.Globalization;
using System.Text.RegularExpressions;

namespace YakUI.Core.Compat;

public enum DistributionPlatform
{
    Any,
    Win,
    Mac,
    Unknown,
}

/// <summary>
/// Метка дистрибутива <c>rh&lt;M&gt;[_&lt;m&gt;]-&lt;win|mac|any&gt;</c> (разделы 3 и 17).
/// Отсутствие метки или <c>any</c> — без ограничений по версии Rhino.
/// Нераспознанная метка не бросает исключение, а помечается <see cref="IsUnknown"/> (NFR-06).
/// </summary>
public sealed partial record DistributionTag
{
    private DistributionTag(int? rhinoMajor, int? rhinoMinor, DistributionPlatform platform, bool versionUnknown, string original)
    {
        RhinoMajor = rhinoMajor;
        RhinoMinor = rhinoMinor;
        Platform = platform;
        IsVersionUnknown = versionUnknown;
        Original = original;
    }

    /// <summary><c>M</c>; null — «any» или неизвестно.</summary>
    public int? RhinoMajor { get; }

    /// <summary><c>m</c>; null, если не указан (по 17.2 считается 0).</summary>
    public int? RhinoMinor { get; }

    public DistributionPlatform Platform { get; }

    public bool IsVersionUnknown { get; }

    public bool IsUnknown => IsVersionUnknown || Platform == DistributionPlatform.Unknown;

    public string Original { get; }

    public static DistributionTag AnyAny { get; } = new(null, null, DistributionPlatform.Any, false, "any-any");

    /// <summary>Метка из полей API: <c>rhino_version</c> (<c>rh8_12</c>, <c>rh7</c>, <c>any</c>, null) и <c>platform</c>.</summary>
    public static DistributionTag FromApi(string? rhinoVersion, string? platform)
    {
        var (major, minor, versionUnknown) = ParseRhinoVersion(rhinoVersion);
        var original = $"{(string.IsNullOrWhiteSpace(rhinoVersion) ? "any" : rhinoVersion.Trim())}-{(string.IsNullOrWhiteSpace(platform) ? "any" : platform.Trim())}";
        return new DistributionTag(major, minor, ParsePlatform(platform), versionUnknown, original);
    }

    /// <summary>Строка <c>rh8_12-win</c>, <c>rh7-any</c>, <c>any-any</c>; пустая — <c>any-any</c>.</summary>
    public static DistributionTag Parse(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return AnyAny;

        var t = tag.Trim();
        var dash = t.LastIndexOf('-');
        return dash < 0 ? FromApi(t, null) : FromApi(t[..dash], t[(dash + 1)..]);
    }

    private static (int? Major, int? Minor, bool Unknown) ParseRhinoVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("any", StringComparison.OrdinalIgnoreCase))
            return (null, null, false);

        var m = RhinoVersionRegex().Match(value.Trim());
        if (!m.Success)
            return (null, null, true);

        var major = int.Parse(m.Groups["major"].Value, CultureInfo.InvariantCulture);
        int? minor = m.Groups["minor"].Success ? int.Parse(m.Groups["minor"].Value, CultureInfo.InvariantCulture) : null;
        return (major, minor, false);
    }

    private static DistributionPlatform ParsePlatform(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? DistributionPlatform.Any
            : value.Trim().ToLowerInvariant() switch
            {
                "any" => DistributionPlatform.Any,
                "win" => DistributionPlatform.Win,
                "mac" => DistributionPlatform.Mac,
                _ => DistributionPlatform.Unknown,
            };

    public override string ToString() => Original;

    [GeneratedRegex(@"^rh(?<major>\d+)(?:_(?<minor>\d+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RhinoVersionRegex();
}
