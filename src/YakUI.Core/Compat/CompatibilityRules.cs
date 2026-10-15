namespace YakUI.Core.Compat;

/// <summary>Статус совместимости дистрибутива или версии пакета с целью (раздел 17, FR-22).</summary>
public enum CompatibilityStatus
{
    /// <summary>Нативный: M = T и m ≤ t.</summary>
    Native,

    /// <summary>Совместимый снизу: M &lt; T; работа не гарантируется.</summary>
    BackwardCompatible,

    /// <summary>Без ограничений: метка <c>any</c> или её отсутствие.</summary>
    Unrestricted,

    /// <summary>Метка или платформа не распознаны (NFR-06).</summary>
    Unknown,

    /// <summary>Несовместимый: M &gt; T, M = T и m &gt; t, или чужая платформа.</summary>
    Incompatible,
}

public sealed record CompatibilityResult(
    CompatibilityStatus Status,
    bool PlatformMismatch = false,
    bool NotGuaranteed = false,
    bool RequiresNetTargetCheck = false);

/// <summary>Правила совместимости раздела 17. Платформа цели — Windows (раздел 2).</summary>
public static class CompatibilityRules
{
    public const DistributionPlatform TargetPlatform = DistributionPlatform.Win;

    /// <summary>
    /// Порядок «лучше → хуже» для 17.6. Правило явно упорядочивает только Нативный и Совместимый;
    /// «Без ограничений» ставится после них: дистрибутив под конкретную версию Rhino точнее универсального.
    /// </summary>
    public static int Rank(CompatibilityStatus status) => status switch
    {
        CompatibilityStatus.Native => 0,
        CompatibilityStatus.BackwardCompatible => 1,
        CompatibilityStatus.Unrestricted => 2,
        CompatibilityStatus.Unknown => 3,
        _ => 4,
    };

    /// <summary>Пункты 17.1–17.5 для одного дистрибутива.</summary>
    public static CompatibilityResult Evaluate(DistributionTag tag, RhinoRelease target)
    {
        // 17.1 — платформа.
        if (tag.Platform == DistributionPlatform.Unknown)
            return new(CompatibilityStatus.Unknown);
        if (tag.Platform != DistributionPlatform.Any && tag.Platform != TargetPlatform)
            return new(CompatibilityStatus.Incompatible, PlatformMismatch: true);

        if (tag.IsVersionUnknown)
            return new(CompatibilityStatus.Unknown);

        // 17.5 — any или отсутствие метки.
        if (tag.RhinoMajor is not { } m)
            return new(CompatibilityStatus.Unrestricted);

        var minor = tag.RhinoMinor ?? 0;

        // 17.2 — нативный.
        if (m == target.Major && minor <= target.Minor)
            return new(CompatibilityStatus.Native);

        // 17.3 — совместимый снизу; для T ≥ 8 нужна проверка net-таргета (по инспекции архива, этап 2).
        if (m < target.Major)
            return new(CompatibilityStatus.BackwardCompatible, NotGuaranteed: true, RequiresNetTargetCheck: target.Major >= 8);

        // 17.4 — несовместимый.
        return new(CompatibilityStatus.Incompatible);
    }

    /// <summary>17.6 — статус версии: лучший из статусов её дистрибутивов; без дистрибутивов — Unknown.</summary>
    public static CompatibilityStatus EvaluateVersion(IEnumerable<DistributionTag> tags, RhinoRelease target)
    {
        CompatibilityStatus? best = null;
        foreach (var tag in tags)
        {
            var s = Evaluate(tag, target).Status;
            if (best is null || Rank(s) < Rank(best.Value))
                best = s;
        }

        return best ?? CompatibilityStatus.Unknown;
    }

    /// <summary>
    /// 17.6 — дистрибутив для установки: Нативный с максимальным m, затем Совместимый с максимальным M
    /// (при равенстве — с максимальным m), затем «Без ограничений». Несовместимые и неизвестные не выбираются.
    /// </summary>
    public static T? SelectBest<T>(IEnumerable<T> items, Func<T, DistributionTag> tagOf, RhinoRelease target)
        where T : class
    {
        T? best = null;
        (int Rank, int Major, int Minor) bestKey = default;

        foreach (var item in items)
        {
            var tag = tagOf(item);
            var status = Evaluate(tag, target).Status;
            if (status is CompatibilityStatus.Incompatible or CompatibilityStatus.Unknown)
                continue;

            var key = (Rank(status), tag.RhinoMajor ?? 0, tag.RhinoMinor ?? 0);
            if (best is null || IsBetter(key, bestKey))
            {
                best = item;
                bestKey = key;
            }
        }

        return best;
    }

    private static bool IsBetter((int Rank, int Major, int Minor) a, (int Rank, int Major, int Minor) b)
    {
        if (a.Rank != b.Rank)
            return a.Rank < b.Rank;
        if (a.Major != b.Major)
            return a.Major > b.Major;
        return a.Minor > b.Minor;
    }
}
