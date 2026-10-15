using System.Globalization;

namespace YakUI.Core.Compat;

/// <summary>Версия Rhino <c>T.t</c> (major.сервис-релиз), для которой считается совместимость (раздел 17).</summary>
public readonly record struct RhinoRelease(int Major, int Minor) : IComparable<RhinoRelease>
{
    public int CompareTo(RhinoRelease other) =>
        Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

    public static RhinoRelease FromVersion(Version v) => new(v.Major, Math.Max(v.Minor, 0));

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");
}
