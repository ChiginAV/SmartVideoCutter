using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartVideoCutter.Models.Video;

public partial class Keyframe : ObservableObject
{
    [ObservableProperty] private int _index;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private double _timestamp;
}