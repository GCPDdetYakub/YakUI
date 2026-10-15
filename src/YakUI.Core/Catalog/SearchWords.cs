namespace YakUI.Core.Catalog;

/// <summary>
/// Слова для поиска (FR-25): текст режется на слова по небуквенно-цифровым символам и по границам camelCase
/// (<c>igMesh</c> → <c>ig</c>, <c>mesh</c>; <c>MeshEdit-Components</c> → <c>mesh</c>, <c>edit</c>, <c>components</c>).
/// Совпадение — по началу слова: <c>attic</c> находит «Attic», но не «lattice».
/// </summary>
public static class SearchWords
{
    public static string[] Split(string? text, bool camelCase = true)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var words = new List<string>();
        var start = -1;     // начало части camelCase
        var token = -1;     // начало целого токена: «EleFront» ищется и как «elefront»
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : ' ';
            var isWordChar = char.IsLetterOrDigit(c);
            if (start >= 0 && (!isWordChar || (camelCase && IsCamelBoundary(text, i))))
            {
                words.Add(text[start..i].ToLowerInvariant());
                start = -1;
            }

            if (token >= 0 && !isWordChar)
            {
                words.Add(text[token..i].ToLowerInvariant());
                token = -1;
            }

            if (isWordChar && start < 0)
                start = i;
            if (isWordChar && token < 0)
                token = i;
        }

        return [.. words.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Слова запроса: те же разделители, без деления camelCase (запрос пишут как попало).</summary>
    public static string[] Terms(string? query) => Split(query, camelCase: false);

    /// <summary>Каждое слово запроса — начало какого-либо слова текста.</summary>
    public static bool MatchesAll(IReadOnlyList<string> words, IReadOnlyList<string> terms)
    {
        foreach (var t in terms)
        {
            var found = false;
            foreach (var w in words)
            {
                if (w.StartsWith(t, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                return false;
        }

        return true;
    }

    // «aB» или «ABc» (конец аббревиатуры: «XMLParser» → xml, parser), а также переход буква → цифра не делим.
    private static bool IsCamelBoundary(string s, int i)
    {
        if (i <= 0 || i >= s.Length)
            return false;
        var prev = s[i - 1];
        var cur = s[i];
        if (char.IsLower(prev) && char.IsUpper(cur))
            return true;
        return char.IsUpper(prev) && char.IsUpper(cur) && i + 1 < s.Length && char.IsLower(s[i + 1]);
    }
}
