namespace SmartVideoCutterFFmpeg.Models.ComputerVision;

/// Рамка лица в нормализованных координатах (0..1) относительно кадра.
public class FaceBox
{
    /// Индекс рамки в списке (параметр SelectFaceCommand).
    public int Index { get; init; }

    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; }
    public double H { get; init; }
}
