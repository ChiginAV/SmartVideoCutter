using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartVideoCutterFFmpeg.Models;

public partial class Settings : ObservableObject
{
    [ObservableProperty] private string _arcFacePath = string.Empty;

    [ObservableProperty] private string _yoloPath = string.Empty;

    [ObservableProperty] private string _ffmpegPath = string.Empty;

    [ObservableProperty] private AppThemeMode _themeMode = AppThemeMode.Dark;
}