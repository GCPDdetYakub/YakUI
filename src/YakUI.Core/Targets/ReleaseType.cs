using System.Text.RegularExpressions;

namespace YakUI.Core.Targets;

/// <summary>Тип выпуска Rhino (FR-81g).</summary>
public enum RhinoReleaseType
{
    ServiceRelease,
    ReleaseCandidate,
    Beta,
    Wip,
}

/// <summary>Где искать признак выпуска (порядок проверки FR-81g).</summary>
public enum ReleaseEvidenceSource
{
    /// <summary>Сведения о версии <c>Rhino.exe</c>: FileDescription, SpecialBuild, ProductName.</summary>
    VersionInfo,

    /// <summary>Имя папки установки.</summary>
    FolderName,

    /// <summary>Значение реестра <c>…\Rhinoceros\&lt;N.0&gt;\Install\BuildType</c>.</summary>
    BuildType,
}

/// <summary>Правило из файла настроек: регулярное выражение (без учёта регистра) → тип выпуска.</summary>
public sealed record ReleaseTypeRule(string Pattern, RhinoReleaseType Type);

/// <summary>Поля, по которым определяется тип выпуска; выводятся в <c>YakUI.Cli env</c>.</summary>
public sealed record ReleaseEvidence(
    string? FileDescription,
    string? SpecialBuild,
    string? ProductName,
    string? FolderName,
    string? BuildType);

/// <summary>Итог: тип и что именно совпало (для отчёта).</summary>
public sealed record ReleaseTypeResult(RhinoReleaseType Type, ReleaseEvidenceSource? Source, string? MatchedText)
{
    public bool IsPrerelease => Type != RhinoReleaseType.ServiceRelease;
}

/// <summary>
/// Определение типа выпуска по правилам из настроек (FR-81g): сначала сведения о версии <c>Rhino.exe</c>,
/// затем имя папки, затем <c>BuildType</c> (<c>COMMERCIAL</c> — обычный выпуск; другое значение даёт пометку,
/// только если его распознали правила). Ничего не совпало — обычный сервис-релиз.
/// </summary>
public sealed class ReleaseTypeRules
{
    public const string Commercial = "COMMERCIAL";

    /// <summary>Правила по умолчанию; правятся в файле настроек без пересборки.</summary>
    public static readonly IReadOnlyList<ReleaseTypeRule> Defaults =
    [
        new(@"\bWIP\b", RhinoReleaseType.Wip),
        new(@"\bBeta\b", RhinoReleaseType.Beta),
        new(@"Release\s+Candidate|\bRC\b", RhinoReleaseType.ReleaseCandidate),
    ];

    private readonly List<(Regex Regex, RhinoReleaseType Type)> _rules;

    public ReleaseTypeRules(IEnumerable<ReleaseTypeRule> rules)
    {
        _rules = [];
        foreach (var r in rules)
        {
            try
            {
                _rules.Add((new Regex(r.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)), r.Type));
            }
            catch (ArgumentException)
            {
                // Ошибочное правило в настройках не ломает обнаружение Rhino (NFR-06).
            }
        }
    }

    public ReleaseTypeResult Detect(ReleaseEvidence e)
    {
        foreach (var text in new[] { e.FileDescription, e.SpecialBuild, e.ProductName })
        {
            if (Match(text) is { } t)
                return new ReleaseTypeResult(t, ReleaseEvidenceSource.VersionInfo, text);
        }

        if (Match(e.FolderName) is { } f)
            return new ReleaseTypeResult(f, ReleaseEvidenceSource.FolderName, e.FolderName);

        if (!string.IsNullOrWhiteSpace(e.BuildType)
            && !e.BuildType.Trim().Equals(Commercial, StringComparison.OrdinalIgnoreCase)
            && Match(e.BuildType) is { } b)
            return new ReleaseTypeResult(b, ReleaseEvidenceSource.BuildType, e.BuildType);

        return new ReleaseTypeResult(RhinoReleaseType.ServiceRelease, null, null);
    }

    private RhinoReleaseType? Match(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        foreach (var (regex, type) in _rules)
        {
            try
            {
                if (regex.IsMatch(text))
                    return type;
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }

        return null;
    }
}
