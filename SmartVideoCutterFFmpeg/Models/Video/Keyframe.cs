using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartVideoCutterFFmpeg.Models.Video;

public partial class Keyframe : ObservableObject
{
    [ObservableProperty] private int _index;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private double _timestamp;
}