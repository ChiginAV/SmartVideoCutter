using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SmartVideoCutterVLC.Models;

public class Settings : INotifyPropertyChanged
{
    private string _arcfacePath = string.Empty;
    private string _yoloPath = string.Empty;
    private string _ffmpegPath = string.Empty;
    
    public string ArcFacePath
    {
        get => _arcfacePath;
        set { _arcfacePath = value; OnPropertyChanged(); }
    }
    
    public string YoloPath
    {
        get => _yoloPath;
        set { _yoloPath = value; OnPropertyChanged(); }
    }
    
    public string FfmpegPath
    {
        get => _ffmpegPath;
        set { _ffmpegPath = value; OnPropertyChanged(); }
    }
    
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}