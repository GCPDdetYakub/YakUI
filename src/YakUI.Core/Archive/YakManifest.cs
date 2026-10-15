using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YakUI.Core.Localization;

namespace YakUI.Core.Archive;

/// <summary>
/// <c>manifest.yml</c> пакета Yak: name, version, authors, description, url, icon, keywords.
/// Неизвестные поля игнорируются; отдельная строка вместо списка (authors, keywords) тоже принимается.
/// </summary>
public sealed record YakManifest
{
    public string? Name { get; init; }
    public string? Version { get; init; }
    public IReadOnlyList<string> Authors { get; init; } = [];
    public string? Description { get; init; }
    public string? Url { get; init; }
    public string? Icon { get; init; }
    public string? IconUrl { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = [];

    public static YakManifest Parse(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException e)
        {
            throw new InvalidDataException($"manifest.yml: {e.Message}", e);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException(L.T("YakManifest.18", $"manifest.yml: ожидается словарь полей."));

        string? Scalar(string key) =>
            root.Children.TryGetValue(new YamlScalarNode(key), out var n) && n is YamlScalarNode s && !string.IsNullOrWhiteSpace(s.Value) ? s.Value.Trim() : null;

        IReadOnlyList<string> List(string key)
        {
            if (!root.Children.TryGetValue(new YamlScalarNode(key), out var n))
                return [];
            return n switch
            {
                YamlSequenceNode seq => seq.Children.OfType<YamlScalarNode>().Select(x => x.Value?.Trim()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList(),
                YamlScalarNode s when !string.IsNullOrWhiteSpace(s.Value) => s.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                _ => [],
            };
        }

        return new YakManifest
        {
            Name = Scalar("name"),
            Version = Scalar("version"),
            Authors = List("authors"),
            Description = Scalar("description"),
            Url = Scalar("url"),
            Icon = Scalar("icon"),
            IconUrl = Scalar("icon_url"),
            Keywords = List("keywords"),
        };
    }
}
