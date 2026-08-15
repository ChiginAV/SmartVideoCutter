using System.IO;
using System.Text.Json;


namespace SmartVideoCutterFlyleaf.Services;

public static class SettingsManager
{
    private static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static Settings CurrentSettings { get; set; } = new Settings();

    public static void Load()
    {
        if (File.Exists(ConfigPath))
        {
            string json = File.ReadAllText(ConfigPath);

            CurrentSettings = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
        }
        else
        {
            CurrentSettings = new Settings();

            Save(); // Создаем файл с дефолтными настройками
        }
    }

    // Сохранение в файл
    public static void Save()
    {
        var options = new JsonSerializerOptions { WriteIndented = true }; // Красивый отступ JSON
        string json = JsonSerializer.Serialize(CurrentSettings, options);

        File.WriteAllText(ConfigPath, json);
    }
}