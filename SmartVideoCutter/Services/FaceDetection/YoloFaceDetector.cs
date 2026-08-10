using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace SmartVideoCutter.Services.FaceDetection;

public class YoloFaceDetector
{
    private readonly InferenceSession _session;
    private readonly TensorConverter _converter;

    public YoloFaceDetector(string modelPath, SessionOptions options)
    {
        _session = new InferenceSession(modelPath, options);
        _converter = new TensorConverter(YoloResultParser.Width, YoloResultParser.Height);
    }

    /// Находит лица в кадре через YOLO-Face.
    public List<Rect> Detect(Mat frame)
    {
        var sw = Stopwatch.StartNew();

        Mat resized = new Mat();
        Cv2.Resize(frame, resized, new OpenCvSharp.Size(YoloResultParser.Width, YoloResultParser.Height));
        double resizeMs = sw.Elapsed.TotalMilliseconds;

        var inputTensor = _converter.ConvertToNchw(resized, YoloResultParser.Width, YoloResultParser.Height);
        double convertMs = sw.Elapsed.TotalMilliseconds - resizeMs;

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("images", inputTensor) };
        using var results = _session.Run(inputs);
        double inferenceMs = sw.Elapsed.TotalMilliseconds - resizeMs - convertMs;

        Debug.WriteLine(
            $"[DetectFaces] Resize:{resizeMs:F1}ms Convert:{convertMs:F1}ms Inference:{inferenceMs:F1}ms Total:{sw.Elapsed.TotalMilliseconds:F1}ms");

        return YoloResultParser.Parse(results, frame.Width, frame.Height);
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}