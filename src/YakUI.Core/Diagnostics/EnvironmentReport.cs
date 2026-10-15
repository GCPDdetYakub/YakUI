using System.Globalization;
using System.Text;
using YakUI.Core.Platform;
using YakUI.Core.Settings;
using YakUI.Core.Targets;
using YakUI.Core.Tools;

namespace YakUI.Core.Diagnostics;

/// <summary>Текстовый отчёт об окружении (FR-06): версии, пути, среды .NET, yak-инструменты.</summary>
public static class EnvironmentReport
{
    public static string Build(
        IReadOnlyList<RhinoTarget> targets,
        IReadOnlyList<YakTool> tools,
        Func<int, YakTool?> selectFor,
        IPlatformPaths paths,
        AppSettings settings,
        DateTimeOffset now)
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;

        sb.AppendLine(ci, $"YakUI {AppInfo.Version} — отчёт об окружении ({now.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz})");
        sb.AppendLine(ci, $"ОС: {Environment.OSVersion.VersionString}, {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        sb.AppendLine();

        sb.AppendLine("Данные YakUI");
        sb.AppendLine(ci, $"  Режим:      {(paths.IsPortable ? "portable (.\\data)" : "профиль Windows")}");
        sb.AppendLine(ci, $"  Настройки:  {paths.ConfigDirectory}");
        sb.AppendLine(ci, $"  Данные:     {paths.DataDirectory}");
        sb.AppendLine(ci, $"  Сервер Yak: {settings.ApiBaseUrl}");
        sb.AppendLine();

        sb.AppendLine(ci, $"Rhino ({targets.Count})");
        if (targets.Count == 0)
            sb.AppendLine("  Установленные Rhino 6+ не найдены.");

        foreach (var t in targets)
        {
            sb.AppendLine(ci, $"  {t.DisplayName}");
            sb.AppendLine(ci, $"    Версия:             {t.Version}");
            sb.AppendLine(ci, $"    Тип выпуска:        {ReleaseName(t.ReleaseType)}{MatchNote(t)}");
            if (t.ReleaseEvidence is { } ev)
            {
                // FR-81g: поля для сбора данных с RC/WIP.
                sb.AppendLine(ci, $"      FileDescription:  {ev.FileDescription ?? "—"}");
                sb.AppendLine(ci, $"      SpecialBuild:     {ev.SpecialBuild ?? "—"}");
                sb.AppendLine(ci, $"      ProductName:      {ev.ProductName ?? "—"}");
                sb.AppendLine(ci, $"      Папка:            {ev.FolderName ?? "—"}");
                sb.AppendLine(ci, $"      BuildType:        {ev.BuildType ?? "—"}");
            }
            sb.AppendLine(ci, $"    Путь:               {t.InstallPath}  [{(t.Source == TargetSource.Registry ? "реестр" : "Program Files")}]");
            sb.AppendLine(ci, $"    Среда .NET:         {t.Runtime.DisplayName}{RuntimeNote(t)}");
            sb.AppendLine(ci, $"    Профиль пакетов:    {t.UserPackagesDirectory}");
            sb.AppendLine(ci, $"    Для всех (чтение):  {t.AllUsersPackagesDirectory}");
            sb.AppendLine(ci, $"    GH Libraries:       {t.GrasshopperLibrariesDirectory}");
            sb.AppendLine(ci, $"    Встроенный yak:     {t.BuiltInYakPath ?? "нет"}");
            var chosen = selectFor(t.Major);
            sb.AppendLine(ci, $"    yak для установки:  {(chosen is null ? "нет подходящего (стандартная установка через yak недоступна, AD-10)" : $"{chosen.Version} — {chosen.Path}")}");
        }

        sb.AppendLine();
        sb.AppendLine(ci, $"yak-инструменты ({tools.Count})");
        foreach (var group in tools.GroupBy(t => t.BoundRhinoMajor).OrderByDescending(g => g.Key ?? -1))
        {
            sb.AppendLine(group.Key is { } major ? string.Create(ci, $"  Привязаны к Rhino {major}:") : "  Привязка не определена:");
            foreach (var tool in group.OrderByDescending(t => t.Version))
            {
                sb.AppendLine(ci, $"    {tool.Version?.ToString() ?? "?"}  {OriginName(tool.Origin)}  {tool.Path}");
                sb.AppendLine(ci, $"      Подпись: {SignatureText(tool)}");
                sb.AppendLine(ci, $"      .NET:    {RuntimeText(tool.Runtime)}");
                if (tool.PackageDirectorySuffix is not null)
                    sb.AppendLine(ci, $"      Каталог: …\\{tool.PackageDirectorySuffix}");
                if (tool.VersionInfo?.RhinoVersion is { } rv)
                    sb.AppendLine(ci, $"      Сообщает Rhino: {rv}");
                if (tool.Problem is not null)
                    sb.AppendLine(ci, $"      Не используется: {tool.Problem}");
            }
        }

        return sb.ToString();
    }

    private static string ReleaseName(RhinoReleaseType t) => t switch
    {
        RhinoReleaseType.Wip => "WIP",
        RhinoReleaseType.Beta => "Beta",
        RhinoReleaseType.ReleaseCandidate => "Release Candidate",
        _ => "сервис-релиз",
    };

    private static string MatchNote(RhinoTarget t)
    {
        if (t.ReleaseTypeMatch is not { Source: { } src } m)
            return "";
        var where = src switch
        {
            ReleaseEvidenceSource.VersionInfo => "сведениям о версии Rhino.exe",
            ReleaseEvidenceSource.FolderName => "имени папки",
            _ => "BuildType",
        };
        return $" (по {where}: «{m.MatchedText}»)";
    }

    private static string RuntimeNote(RhinoTarget t) =>
        t.Runtime.NetFrameworkOptional ? " (по умолчанию; режим SetDotNetRuntime не определён, ОВ-17)" : "";

    private static string OriginName(YakToolOrigin o) => o switch
    {
        YakToolOrigin.BuiltIn => "встроенный",
        YakToolOrigin.Downloaded => "скачанный",
        _ => "указан вручную",
    };

    private static string SignatureText(YakTool t) => t.Trust switch
    {
        YakTrust.SignedByMcNeel => $"McNeel, действительна ({t.Signature.Publisher})",
        YakTrust.UnsignedAccepted => $"без подписи, подтверждён пользователем (SHA-256 {t.Sha256})",
        YakTrust.UnsignedDownloaded => $"без подписи, скачан YakUI с files.mcneel.com по HTTPS (SHA-256 {t.Sha256})",
        YakTrust.NeedsConfirmation => $"без подписи, не подтверждён (SHA-256 {t.Sha256})",
        _ => $"не принята: {t.Signature.State}, издатель {t.Signature.Publisher ?? "—"}",
    };

    private static string RuntimeText(RuntimeRequirement r)
    {
        if (!r.FrameworkDependent)
            return "не требует установленного .NET";

        var parts = r.Frameworks.Select(f => f.Installed
            ? $"{f.Name} ≥ {f.Version} → {f.Resolved}"
            : $"{f.Name} ≥ {f.Version} → НЕ НАЙДЕН");
        return string.Join("; ", parts);
    }
}
