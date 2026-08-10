using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace SmartVideoCutterWPF.Services;

public class YoloFaceDetector
{
    private readonly InferenceSession _inferenceSession;

    public YoloFaceDetector()
    {
        _inferenceSession = OpenInferenceSession();
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

    public List<Rect> Detect(Mat frame)
    {
        return new List<Rect>(); // Заглушка
    }

    public void Dispose()
    {
        _inferenceSession?.Dispose();
    }
}