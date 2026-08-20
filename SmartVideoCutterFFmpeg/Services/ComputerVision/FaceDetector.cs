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

/// Детекция лиц (SCRFD) через FaceAiSharp.
/// InferenceSession не потокобезопасен — сериализуем вызовы lock'ом.
public sealed class FaceDetector : IDisposable
{
    private readonly IFaceDetectorWithLandmarks _detector;
    private readonly object _lock = new();

    public FaceDetector()
    {
        var options = new SessionOptions();
        if (OrtEnv.Instance().GetAvailableProviders().Contains("DmlExecutionProvider"))
            options.AppendExecutionProvider_DML();
        // иначе CPU (дефолт)

        _detector = FaceAiSharpBundleFactory.CreateFaceDetectorWithLandmarks(options);
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

    public void Dispose()
    {
        if (_detector is IDisposable d)
            d.Dispose();
    }
}
