using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace SmartVideoCutterFFmpeg.Services.FaceDetection;

public class YoloFaceDetector : IDisposable
{
    #region Properties

    private readonly InferenceSession _inferenceSession;
    private readonly TensorConverter _tensorConverter;
    private readonly object _lock = new();

    #endregion

    public YoloFaceDetector()
    {
        _inferenceSession = OpenInferenceSession();
        _tensorConverter = new TensorConverter(YoloResultParser.Width, YoloResultParser.Height);
    }

    private InferenceSession OpenInferenceSession()
    {
        var options = new SessionOptions(); // Microsoft.ML.OnnxRuntime

        var availableProviders = OrtEnv.Instance().GetAvailableProviders();

        if (availableProviders.Contains("TensorrtExecutionProvider"))
        {
            options.AppendExecutionProvider_Tensorrt();
        }
        else if (availableProviders.Contains("CUDAExecutionProvider"))
        {
            options.AppendExecutionProvider_CUDA();
        }
        else if (availableProviders.Contains("DmlExecutionProvider"))
        {
            options.AppendExecutionProvider_DML();
        }
        else // CPUExecutionProvider
        {
            options.AppendExecutionProvider_CPU();
        }

        return new InferenceSession(SettingsManager.CurrentSettings.YoloPath, options);
    }

    /// Находит лица в кадре, возвращает рамки в пикселях исходного кадра.
    /// InferenceSession и буферы TensorConverter не потокобезопасны —
    /// сериализуем весь вызов, иначе параллельный Run даёт 0xC0000005.
    public List<Rect> Detect(Mat frame)
    {
        lock (_lock)
        {
            using var resized = new Mat();
            Cv2.Resize(frame, resized, new Size(YoloResultParser.Width, YoloResultParser.Height));

            var input = _tensorConverter.ConvertToNchw(resized, YoloResultParser.Width, YoloResultParser.Height);
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("images", input) };

            using var results = _inferenceSession.Run(inputs);
            return YoloResultParser.Parse(results, frame.Width, frame.Height);
        }
    }


    public void Dispose()
    {
        _inferenceSession?.Dispose();
    }
}