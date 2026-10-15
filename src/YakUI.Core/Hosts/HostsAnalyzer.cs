namespace YakUI.Core.Hosts;

public enum HostsLineState
{
    Active,
    Commented,
    Missing,
}

/// <summary>Состояние контрольной строки и номер строки в файле (с 1); у отсутствующей — null (FR-89c, FR-89g).</summary>
public sealed record HostsLineStatus(HostsEntry Entry, HostsLineState State, int? LineNumber);

/// <summary>
/// Состояние контрольных строк в hosts (FR-89c): сравнение по паре «IP + имя» без учёта регистра имени и числа пробелов;
/// закомментированная — <c>#</c> в начале (пробелы перед ним допускаются); хвостовой комментарий игнорируется.
/// Если строка встречается и активной, и закомментированной, — она активна.
/// </summary>
public static class HostsAnalyzer
{
    public static IReadOnlyList<HostsLineStatus> Analyze(string content, IEnumerable<HostsEntry> watched)
    {
        var lines = content.Split('\n');
        var result = new List<HostsLineStatus>();
        foreach (var entry in watched)
        {
            HostsLineStatus? commented = null;
            HostsLineStatus? active = null;
            for (var i = 0; i < lines.Length && active is null; i++)
            {
                var line = lines[i].TrimEnd('\r').TrimStart(' ', '\t');
                var isComment = line.StartsWith('#');
                if (isComment)
                    line = line.TrimStart('#');
                var hash = line.IndexOf('#', StringComparison.Ordinal);
                if (hash >= 0)
                    line = line[..hash];

                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !HostsEntry.TryParseIp(parts[0], out var ip))
                    continue;
                if (!parts.Skip(1).Any(h => entry.Matches(ip, h)))
                    continue;

                if (isComment)
                    commented ??= new HostsLineStatus(entry, HostsLineState.Commented, i + 1);
                else
                    active = new HostsLineStatus(entry, HostsLineState.Active, i + 1);
            }

            result.Add(active ?? commented ?? new HostsLineStatus(entry, HostsLineState.Missing, null));
        }

        return result;
    }

    /// <summary>Разбор списка из настроек; некорректные строки пропускаются.</summary>
    public static IReadOnlyList<HostsEntry> ParseList(IEnumerable<string>? list) =>
        (list ?? []).Select(s => HostsEntry.TryParse(s, out var e, out _) ? e : null).OfType<HostsEntry>().ToList();
}
