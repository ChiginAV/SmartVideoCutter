namespace SmartVideoCutterFFmpeg.Models.Video;

public class Keyframe
{
    public int Index { get; set; }
    public bool IsSelected { get; set; }
    public double Timestamp { get; set; }
}