using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace SmartVideoCutterFlyleaf.Services.FaceDetection;

public static class YoloResultParser
{
    public const int Width = 640;
    public const int Height = 640;

    /// Порог уверенности для детекции лиц (YOLO-Face).
    /// Max sigmoid(conf) ≈ 0.66 при наличии лица, поэтому порог 0.6.
    public const float ConfidenceThreshold = 0.6f;

    // Sigmoid: σ(x) = 1 / (1 + e^(-x))
    private static float Sigmoid(float x)
    {
        if (x > 20f) return 1.0f;
        if (x < -20f) return 0.0f;
        return 1.0f / (1.0f + (float)Math.Exp(-x));
    }

    /// Разбирает вывод YOLO-Face (NCHW или NHWC формат) и возвращает рамки лиц.
    public static List<Rect> Parse(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, int origW,
        int origH)
    {
        var output = results.First().AsTensor<float>();
        List<Rect> faces = new List<Rect>();

        // Диагностика: выводим размерность тензора
        string dims = string.Join("x", output.Dimensions.ToArray());
        Debug.WriteLine($"[ParseYoloResults] Tensor shape: [{dims}], Rank={output.Rank}");

        int rank = output.Rank;
        int numPredictions = -1;
        bool formatNCHW = false; // [1, 5+, N] — стандартный YOLOv8
        bool formatNHWC = false; // [1, N, 5+] — альтернативный формат

        if (rank == 3)
        {
            int dim0 = output.Dimensions[0]; // batch=1
            int dim1 = output.Dimensions[1];
            int dim2 = output.Dimensions[2];

            // Определяем формат: если dim2 >> dim1, то [1, features, anchors] (NCHW)
            // Если dim1 >> dim2, то [1, anchors, features] (NHWC)
            if (dim2 > dim1)
            {
                formatNCHW = true;
                numPredictions = dim2;
                Debug.WriteLine($"[ParseYoloResults] Format: NCHW [batch={dim0}, features={dim1}, anchors={dim2}]");
            }
            else
            {
                formatNHWC = true;
                numPredictions = dim1;
                Debug.WriteLine($"[ParseYoloResults] Format: NHWC [batch={dim0}, anchors={dim1}, features={dim2}]");
            }
        }

        // --- NCHW формат: [batch, features, anchors] ---
        if (formatNCHW)
        {
            // отладка
            float maxRaw = float.MinValue, maxSig = 0f;
            int maxIdx = -1;
            // конец отладки

            // YOLO-Face output = [cx, cy, w, h, conf]
            // cx,cy — центр bounding box в пикселях входного изображения (640x640)
            // w,h — ширина и высота bounding box в пикселях
            // conf — логит (требует sigmoid)
            float scaleX = (float)origW / Width;
            float scaleY = (float)origH / Height;

            for (int i = 0; i < numPredictions; i++)
            {
                float cx = output[0, 0, i];
                float cy = output[0, 1, i];
                float w = output[0, 2, i];
                float h = output[0, 3, i];
                float rawConf = output[0, 4, i];

                // Только confidence требует sigmoid
                float conf = Sigmoid(rawConf);

                // отладка
                if (rawConf > maxRaw)
                {
                    maxRaw = rawConf;
                    maxIdx = i;
                }

                if (conf > maxSig) maxSig = conf;
                // конец отладки

                if (conf <= ConfidenceThreshold)
                    continue;

                // cx,cy,w,h — пиксели 640x640 → конвертируем в x0,y0,x1,y1 и масштабируем
                float left = (cx - w * 0.5f) * scaleX;
                float top = (cy - h * 0.5f) * scaleY;
                float right = (cx + w * 0.5f) * scaleX;
                float bottom = (cy + h * 0.5f) * scaleY;

                int x1 = Math.Clamp((int)left, 0, origW - 1);
                int y1 = Math.Clamp((int)top, 0, origH - 1);
                int width = Math.Max(1, (int)(right - left));
                int height = Math.Max(1, (int)(bottom - top));

                faces.Add(new Rect(x1, y1, width, height));
            }

            Debug.WriteLine(
                $"[ParseYoloResults] Max raw conf: {maxRaw:F4} (idx {maxIdx}), max sigmoid: {maxSig:F4}"); // отладка
        }
        // --- NHWC формат: [batch, anchors, features] ---
        else if (formatNHWC)
        {
            int confIdx = Math.Min(4, output.Dimensions[2] - 1);

            for (int i = 0; i < numPredictions; i++)
            {
                float rawConf = output[0, i, confIdx];
                float confidence = Sigmoid(rawConf);

                if (confidence > ConfidenceThreshold)
                {
                    // x1,y1,x2,y2 — нормализованные координаты
                    float x1n = output[0, i, 0];
                    float y1n = output[0, i, 1];
                    float x2n = output[0, i, 2];
                    float y2n = output[0, i, 3];

                    int x1 = (x1n > 1) ? (int)x1n : (int)(x1n * origW);
                    int y1 = (y1n > 1) ? (int)y1n : (int)(y1n * origH);
                    int x2 = (x2n > 1) ? (int)x2n : (int)(x2n * origW);
                    int y2 = (y2n > 1) ? (int)y2n : (int)(y2n * origH);

                    int width = Math.Max(1, x2 - x1);
                    int height = Math.Max(1, y2 - y1);
                    x1 = Math.Clamp(x1, 0, origW - 1);
                    y1 = Math.Clamp(y1, 0, origH - 1);

                    faces.Add(new Rect(x1, y1, width, height));
                }
            }
        }

        Debug.WriteLine($"[ParseYoloResults] Detected {faces.Count} faces before filtering");
        var filtered = NonMaxSuppression.Filter(faces);
        Debug.WriteLine($"[ParseYoloResults] After NMS: {filtered.Count} faces");

        return filtered;
    }
}