namespace YakUI.Core.Archive;

/// <summary>Тип содержимого распакованной папки (FR-56): Rhino (.rhp), Grasshopper (.gha/.ghpy/.ghuser) или оба.</summary>
public static class PackageContentKindDetector
{
    public static PackageContentKind Detect(string directory)
    {
        var exts = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetExtension(f).ToLowerInvariant()).ToHashSet();
        var rhino = exts.Contains(".rhp");
        var gh = exts.Contains(".gha") || exts.Contains(".ghpy") || exts.Contains(".ghuser");
        return (rhino, gh) switch
        {
            (true, true) => PackageContentKind.Mixed,
            (true, false) => PackageContentKind.Rhino,
            (false, true) => PackageContentKind.Grasshopper,
            _ => PackageContentKind.None,
        };
    }
}
