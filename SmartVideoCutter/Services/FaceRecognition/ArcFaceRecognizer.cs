using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;
using System.Diagnostics;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SmartVideoCutter.Services.FaceRecognition;

public class ArcFaceRecognizer
{
    public const int InputSize = 112;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly TensorConverter _converter;

    public ArcFaceRecognizer(string modelPath, SessionOptions options)
    {
        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
        _converter = new TensorConverter(InputSize, InputSize);
    }

    /// <summary>
    /// Превращает лицо в 512-мерный вектор.
    /// </summary>
    public float[] GetEmbedding(Mat faceImage)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Mat resized = new Mat();
        Cv2.Resize(faceImage, resized, new OpenCvSharp.Size(InputSize, InputSize));
        double resizeMs = sw.Elapsed.TotalMilliseconds;

        var inputTensor = _converter.ConvertToNhwc(resized, InputSize);
        double convertMs = sw.Elapsed.TotalMilliseconds - resizeMs;

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, inputTensor) };
        using var results = _session.Run(inputs);
        double inferenceMs = sw.Elapsed.TotalMilliseconds - resizeMs - convertMs;

        System.Diagnostics.Debug.WriteLine($"[GetEmbedding] Resize:{resizeMs:F1}ms Convert:{convertMs:F1}ms Inference:{inferenceMs:F1}ms Total:{sw.Elapsed.TotalMilliseconds:F1}ms");

        return results.First().AsEnumerable<float>().ToArray();
    }

    public void Dispose() { _session?.Dispose(); }
}