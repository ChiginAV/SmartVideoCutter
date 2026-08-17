using OpenCvSharp;

namespace SmartVideoCutterFlyleaf.Models;

/// Выбранное лицо: кроп для ArcFace, рамка в пикселях, таймстамп.
public sealed class SelectedFace : IDisposable
{
    /// BGR-кроп с margin (нативное разрешение) — вход для ArcFace.
    public Mat Crop { get; }

    /// Рамка лица в пикселях исходного кадра (без margin).
    public OpenCvSharp.Rect BoxPx { get; }

    /// CurTime (мс) в момент выбора.
    public long TimestampMs { get; }

    public SelectedFace(Mat crop, OpenCvSharp.Rect boxPx, long timestampMs)
    {
        Crop = crop;
        BoxPx = boxPx;
        TimestampMs = timestampMs;
    }

    public void Dispose() => Crop?.Dispose();
}