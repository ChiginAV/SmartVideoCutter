using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartVideoCutter.Models;

namespace SmartVideoCutter.Services;

public static class SettingsManager
{
    private static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    /// Единственный экземпляр на всё приложение.
    public static Settings CurrentSettings { get; } = new();

    public static void Load()
    {
        if (!File.Exists(ConfigPath))
        {
            Save(); // создаём файл с дефолтными настройками
            return;
        }

        try
        {
            string json = File.ReadAllText(ConfigPath);

            // копируем значения в единственный общий экземпляр —
            // через сеттеры ObservableObject сработает PropertyChanged.
            Settings? loaded = JsonSerializer.Deserialize<Settings>(json, JsonOptions);

            CurrentSettings.FfmpegPath = loaded?.FfmpegPath ?? string.Empty;
            CurrentSettings.ThemeMode = loaded?.ThemeMode ?? AppThemeMode.Dark;
            CurrentSettings.AnalysisAlgorithm = loaded?.AnalysisAlgorithm ?? AppAnalysisAlgorithm.ThreePerSecond;
        }
        catch (JsonException)
        {
            // повреждённый файл — оставляем дефолтные значения
        }
    }

    public static void Save()
    {
        string json = JsonSerializer.Serialize(CurrentSettings, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }
}