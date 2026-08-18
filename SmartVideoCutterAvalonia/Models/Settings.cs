namespace SmartVideoCutterAvalonia.Models;

public partial class Settings : ObservableObject
{
    [ObservableProperty] private string _ffmpegPath = string.Empty;
    [ObservableProperty] private string _yoloPath = string.Empty;
    [ObservableProperty] private string _arcFacePath = string.Empty;
}