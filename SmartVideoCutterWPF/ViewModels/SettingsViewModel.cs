using Microsoft.Win32;

namespace SmartVideoCutterWPF.ViewModels;

public class SettingsViewModel
{
    public string YoloPath { get; set; } = string.Empty;
    public string ArcFacePath { get; set; } = string.Empty;
    public string FfmpegPath { get; set; } = string.Empty;

    public SettingsViewModel()
    {
        Load(); // Загрузить настройки при создании
    }

    public void Load()
    {
        var settings = SettingsManager.CurrentSettings;
        YoloPath = settings.YoloPath;
        ArcFacePath = settings.ArcFacePath;
        FfmpegPath = settings.FfmpegPath;
    }

    public void Save()
    {
        var settings = SettingsManager.CurrentSettings;
        settings.YoloPath = YoloPath;
        settings.ArcFacePath = ArcFacePath;
        settings.FfmpegPath = FfmpegPath;
        SettingsManager.Save();
    }
}