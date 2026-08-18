namespace SmartVideoCutterAvalonia.Models;

public partial class Keyframe : ObservableObject
{
    public int Index { get; set; }

    [ObservableProperty] private bool _isSelected;

    public double Timestamp { get; set; }
}