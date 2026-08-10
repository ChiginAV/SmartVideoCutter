using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace SmartVideoCutter.Services
{
    /// <summary>
    /// Движок компьютерного зрения: детекция лиц (YOLO-Face) и распознавание лиц (ArcFace).
    /// </summary>
    public class VisionEngine : IDisposable
    {
        private YoloFaceDetector _detector;
        private ArcFaceRecognizer _recognizer;

        public List<Rect> DetectFaces(Mat frame) => _detector.Detect(frame);
        public float[] GetEmbedding(Mat faceImage) => _recognizer.GetEmbedding(faceImage);

        public double CalculateSimilarity(float[] v1, float[] v2)
            => FaceSimilarityCalculator.EuclideanDistance(v1, v2);

        public VisionEngine(string yoloPath, string arcFacePath)
        {
            var options = new SessionOptions(); // Microsoft.ML.OnnxRuntime

            try
            {
                options.AppendExecutionProvider("DML");
                System.Diagnostics.Debug.WriteLine("[VisionEngine] DirectML провайдер подключён (GPU)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[VisionEngine] DirectML не доступен, используем CPU: {ex.Message}");
            }

            _detector = new YoloFaceDetector(yoloPath, options);
            _recognizer = new ArcFaceRecognizer(arcFacePath, options);

            System.Diagnostics.Debug.WriteLine("[VisionEngine] Инициализация завершена");
        }

        public void Dispose()
        {
            _detector?.Dispose();
            _recognizer?.Dispose();
        }
    }
}