namespace SmartVideoCutterFlyleaf.Models;

/// Рамка лица в нормализованных координатах (0..1) относительно кадра.
public class FaceBox
{
    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; }
    public double H { get; init; }
}