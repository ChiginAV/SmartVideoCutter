using OpenCvSharp;

namespace SmartVideoCutter.Services.FaceDetection;

public static class NonMaxSuppression
{
    public const double OverlapThreshold = 0.3; // Порог перекрытия для фильтрации дубликатов (IoU)

    /// Убирает перекрывающиеся рамки (NMS-подобная фильтрация).
    public static List<Rect> Filter(List<Rect> rects)
    {
        if (rects.Count == 0) return rects;

        var sorted = rects.OrderByDescending(r => r.Width * r.Height).ToList();
        List<Rect> finalRects = new List<Rect>();

        foreach (var rect in sorted)
        {
            bool isOverlapping = false;
            foreach (var final in finalRects)
            {
                if (rect.IntersectsWith(final))
                {
                    Rect intersection = Rect.Intersect(rect, final);
                    double overlapArea = intersection.Width * intersection.Height;
                    if (overlapArea / (rect.Width * rect.Height) > OverlapThreshold)
                    {
                        isOverlapping = true;
                        break;
                    }
                }
            }
            if (!isOverlapping) finalRects.Add(rect);
        }

        return finalRects;
    }
}