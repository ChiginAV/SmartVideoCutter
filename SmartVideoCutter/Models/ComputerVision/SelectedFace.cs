namespace SmartVideoCutter.Models.ComputerVision;

/// Выбранное лицо: embedding ArcFace (512), рамка в пикселях, таймстамп.
public sealed class SelectedFace
{
    /// Единичный вектор 512 — результат ArcFace для выбранного лица.
    public float[] Embedding { get; }

    /// Рамка лица в пикселях исходного кадра.
    public OpenCvSharp.Rect BoxPx { get; }

    /// CurTime (мс) в момент выбора.
    public long TimestampMs { get; }

    public SelectedFace(float[] embedding, OpenCvSharp.Rect boxPx, long timestampMs)
    {
        Embedding = embedding;
        BoxPx = boxPx;
        TimestampMs = timestampMs;
    }
}