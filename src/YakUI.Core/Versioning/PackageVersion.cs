using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using YakUI.Core.Localization;

namespace YakUI.Core.Versioning;

/// <summary>
/// Версия пакета Yak: SemVer с prerelease и build-метаданными (<c>0.9.0+1-yak-and-mac</c>)
/// и версии с произвольным числом числовых компонентов (<c>2025.5.5.0</c>), см. 21.1.
/// Build-метаданные не участвуют в сравнении (как в SemVer), но сохраняются в <see cref="Original"/>.
/// </summary>
public sealed class PackageVersion : IComparable<PackageVersion>, IEquatable<PackageVersion>
{
    private readonly int[] _numbers;
    private readonly string[] _prerelease;

    private PackageVersion(string original, int[] numbers, string[] prerelease, string? build)
    {
        Original = original;
        _numbers = numbers;
        _prerelease = prerelease;
        Build = build;
    }

    /// <summary>Строка версии как она пришла (из API, <c>yak list</c>, <c>manifest.txt</c>).</summary>
    public string Original { get; }

    public IReadOnlyList<int> Numbers => _numbers;

    public string? Prerelease => _prerelease.Length == 0 ? null : string.Join('.', _prerelease);

    public string? Build { get; }

    public bool IsPrerelease => _prerelease.Length > 0;

    public int Major => _numbers[0];

    public int Minor => _numbers.Length > 1 ? _numbers[1] : 0;

    public static PackageVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException(L.T("PackageVersion.155", $"Не удалось разобрать версию «{text}»."));

    public static bool TryParse(string? text, [NotNullWhen(true)] out PackageVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var original = text.Trim();
        var rest = original;

        string? build = null;
        var plus = rest.IndexOf('+');
        if (plus >= 0)
        {
            build = rest[(plus + 1)..];
            rest = rest[..plus];
            if (build.Length == 0)
                return false;
        }

        var prerelease = Array.Empty<string>();
        var dash = rest.IndexOf('-');
        if (dash >= 0)
        {
            var pre = rest[(dash + 1)..];
            rest = rest[..dash];
            if (pre.Length == 0)
                return false;
            prerelease = pre.Split('.');
            if (prerelease.Any(p => p.Length == 0))
                return false;
        }

        var parts = rest.Split('.');
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        }

        version = new PackageVersion(original, numbers, prerelease, build);
        return true;
    }

    public int CompareTo(PackageVersion? other)
    {
        if (other is null)
            return 1;

        var len = Math.Max(_numbers.Length, other._numbers.Length);
        for (var i = 0; i < len; i++)
        {
            var a = i < _numbers.Length ? _numbers[i] : 0;
            var b = i < other._numbers.Length ? other._numbers[i] : 0;
            if (a != b)
                return a.CompareTo(b);
        }

        // Релиз старше любого prerelease той же версии.
        if (_prerelease.Length == 0 && other._prerelease.Length == 0)
            return 0;
        if (_prerelease.Length == 0)
            return 1;
        if (other._prerelease.Length == 0)
            return -1;

        var plen = Math.Min(_prerelease.Length, other._prerelease.Length);
        for (var i = 0; i < plen; i++)
        {
            var c = ComparePrereleaseIdentifier(_prerelease[i], other._prerelease[i]);
            if (c != 0)
                return c;
        }

        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    private static int ComparePrereleaseIdentifier(string a, string b)
    {
        var aNum = int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var ai);
        var bNum = int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var bi);
        return (aNum, bNum) switch
        {
            (true, true) => ai.CompareTo(bi),
            (true, false) => -1,
            (false, true) => 1,
            _ => string.CompareOrdinal(a, b),
        };
    }

    public bool Equals(PackageVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is PackageVersion v && Equals(v);

    public override int GetHashCode()
    {
        // Совместимо с Equals: хвостовые нули и build не учитываются.
        var hash = new HashCode();
        var last = _numbers.Length - 1;
        while (last > 0 && _numbers[last] == 0)
            last--;
        for (var i = 0; i <= last; i++)
            hash.Add(_numbers[i]);
        foreach (var p in _prerelease)
            hash.Add(p, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    public override string ToString() => Original;

    public static bool operator ==(PackageVersion? a, PackageVersion? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(PackageVersion? a, PackageVersion? b) => !(a == b);
    public static bool operator <(PackageVersion a, PackageVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(PackageVersion a, PackageVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(PackageVersion a, PackageVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(PackageVersion a, PackageVersion b) => a.CompareTo(b) >= 0;
}
