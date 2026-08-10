namespace SmartVideoCutterWPF.ViewModels;

public class MainViewModel : IDisposable
{
    private readonly YoloFaceDetector? _faceDetector;

    public MainViewModel()
    {
        _faceDetector = new YoloFaceDetector();
    }

    public void Dispose()
    {
        _faceDetector?.Dispose();
    }
}