using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartVideoCutterFFmpeg.Models;

public partial class Settings : ObservableObject
{
    [ObservableProperty] private string _ffmpegPath = string.Empty;

    [ObservableProperty] private AppThemeMode _themeMode = AppThemeMode.Dark;

    [ObservableProperty] private AppAnalysisAlgorithm _analysisAlgorithm = AppAnalysisAlgorithm.ThreePerSecond;
}