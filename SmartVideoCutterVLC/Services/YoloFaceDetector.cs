using Microsoft.ML.OnnxRuntime;

namespace SmartVideoCutterVLC.Services;

public class YoloFaceDetector : IDisposable
{
    #region Properties

    private readonly InferenceSession _inferenceSession;

   #endregion

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

    public void Dispose()
    {
        _inferenceSession?.Dispose();
    }
}