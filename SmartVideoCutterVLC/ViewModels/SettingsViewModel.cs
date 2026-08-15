using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SmartVideoCutterVLC.Services;

namespace SmartVideoCutterVLC.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    public Window? OwnerWindow { get; set; }

    [ObservableProperty] public string _yoloPath;
    [ObservableProperty] public string _arcFacePath;
    [ObservableProperty] public string _ffmpegPath;

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

    [RelayCommand]
    private void Save()
    {
        var settings = SettingsManager.CurrentSettings;
        settings.YoloPath = YoloPath;
        settings.ArcFacePath = ArcFacePath;
        settings.FfmpegPath = FfmpegPath;
        SettingsManager.Save();

        OwnerWindow?.Close();
    }

    [RelayCommand]
    private void BrowseFile(object? parameter)
    {
        var dialog = new OpenFileDialog { Filter = "Файлы|*.onnx;*.pt;*.bin;*.*|Все файлы|*.*" };

        if (dialog.ShowDialog() == true)
        {
            if (parameter.Equals("YoloPath"))
                YoloPath = dialog.FileName;
            else if (parameter.Equals("ArcFacePath"))
                ArcFacePath = dialog.FileName;
        }
    }

    [RelayCommand]
    private void BrowseFolder(object? parameter)
    {
        var dialog = new OpenFolderDialog();

        if (dialog.ShowDialog() == true)
        {
            if (parameter.Equals("FfmpegPath"))
                FfmpegPath = dialog.FolderName;
        }
    }
}