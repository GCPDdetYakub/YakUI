using System.Text.Json;
using System.Text.Json.Serialization;

namespace YakUI.Core.Settings;

/// <summary>Чтение и атомарная запись <c>settings.json</c> (NFR-05).</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public SettingsStore(string configDirectory) => FilePath = Path.Combine(configDirectory, "settings.json");

    public string FilePath { get; }

    /// <summary>Загружает настройки; если файла нет — создаёт его со значениями по умолчанию.</summary>
    public AppSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            var defaults = AppSettings.CreateDefault();
            Save(defaults);
            return defaults;
        }

        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        settings.Normalize();
        return settings;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
