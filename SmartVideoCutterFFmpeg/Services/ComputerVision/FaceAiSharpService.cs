using System.Runtime.InteropServices;
using FaceAiSharp;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace SmartVideoCutterFFmpeg.Services.ComputerVision;

/// Лицо: рамка в пикселях исходного кадра + 5 точек (глаза, нос, уголки рта).
/// PointF здесь — SixLabors.ImageSharp.PointF (FaceAiSharp работает именно с ним).
public sealed record DetectedFace(OpenCvSharp.Rect BoxPx, PointF[]? Landmarks);

/// Детекция (SCRFD) и распознавание (ArcFace) через FaceAiSharp.
/// InferenceSession не потокобезопасен — сериализуем вызовы lock'ом.
public sealed class FaceAiSharpService : IDisposable
{
    private readonly IFaceDetectorWithLandmarks _detector;
    private readonly IFaceEmbeddingsGenerator _embeddings;
    private readonly object _lock = new();

    public FaceAiSharpService()
    {
        var options = new SessionOptions();
        if (OrtEnv.Instance().GetAvailableProviders().Contains("DmlExecutionProvider"))
            options.AppendExecutionProvider_DML();
        // иначе CPU (дефолт)

        _detector = FaceAiSharpBundleFactory.CreateFaceDetectorWithLandmarks(options);
        _embeddings = FaceAiSharpBundleFactory.CreateFaceEmbeddingsGenerator(options);
    }

    /// Лица в кадре: рамки и landmarks в пикселях исходного кадра.
    public List<DetectedFace> Detect(Mat frame)
    {
        lock (_lock)
        {
            using var rgb = new Mat();
            Cv2.CvtColor(frame, rgb, ColorConversionCodes.BGR2RGB); // continuous
            int bytes = rgb.Width * rgb.Height * 3;

            ReadOnlySpan<Rgb24> pixels;
            unsafe
            {
                // zero-copy: span по нативному буферу Mat (img освобождается раньше rgb)
                pixels = MemoryMarshal.Cast<byte, Rgb24>(new Span<byte>((byte*)rgb.Data, bytes));
            }

            using var img = Image.LoadPixelData<Rgb24>(pixels, rgb.Width, rgb.Height);

            return _detector.DetectFaces(img)
                .Select(f => new DetectedFace(
                    new OpenCvSharp.Rect((int)f.Box.X, (int)f.Box.Y, (int)f.Box.Width, (int)f.Box.Height),
                    f.Landmarks?.ToArray()))
                .ToList();
        }
    }

    /// Embedding выбранного лица: кроп с margin + аффинное выравнивание по landmarks.
    public float[] GenerateReferenceEmbedding(Mat frame, OpenCvSharp.Rect boxPx, IReadOnlyList<PointF> landmarks)
    {
        lock (_lock)
        {
            var (crop, cx, cy) = CropWithMargin(frame, boxPx);
            try
            {
                using var rgb = new Mat();
                Cv2.CvtColor(crop, rgb, ColorConversionCodes.BGR2RGB);
                int bytes = rgb.Width * rgb.Height * 3;

                ReadOnlySpan<Rgb24> pixels;
                unsafe
                {
                    // zero-copy: span по нативному буферу Mat (img освобождается раньше rgb)
                    pixels = MemoryMarshal.Cast<byte, Rgb24>(new Span<byte>((byte*)rgb.Data, bytes));
                }

                using var img = Image.LoadPixelData<Rgb24>(pixels, rgb.Width, rgb.Height);

                // landmarks в координатах кадра → координаты кропа
                var local = landmarks
                    .Select(p => new PointF(p.X - cx, p.Y - cy))
                    .ToArray();

                // AlignFaceUsingLandmarks мутирует in-place; img — одноразовая копия
                ArcFaceEmbeddingsGenerator.AlignFaceUsingLandmarks(img, local);
                return _embeddings.GenerateEmbedding(img);
            }
            finally
            {
                crop.Dispose();
            }
        }
    }

    /// Кроп с margin (аффинному преобразованию нужен контекст вокруг лица).
    private static (Mat Crop, int X, int Y) CropWithMargin(Mat frame, OpenCvSharp.Rect box, double margin = 0.35)
    {
        int mx = (int)(box.Width * margin), my = (int)(box.Height * margin);
        int x = Math.Max(0, box.X - mx);
        int y = Math.Max(0, box.Y - my);
        int w = Math.Min(frame.Width - x, box.Width + 2 * mx);
        int h = Math.Min(frame.Height - y, box.Height + 2 * my);
        return (new Mat(frame, new OpenCvSharp.Rect(x, y, w, h)).Clone(), x, y);
    }

    public void Dispose()
    {
        if (_detector is IDisposable d)
            d.Dispose();
        if (_embeddings is IDisposable e)
            e.Dispose();
    }
}