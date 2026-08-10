using OpenCvSharp;
using OpenCvSharp.Extensions;
using SmartVideoCutter.Models;

namespace SmartVideoCutter.Services
{
    /// <summary>
    /// Метаданные видеофайла.
    /// </summary>
    public record VideoInfo(int TotalFrames, int Width, int Height);

    /// <summary>
    /// Результат загрузки кадра: кадр, изображение для UI и найденные люди.
    /// </summary>
    public record FrameResult(Mat Frame, Bitmap Image, List<Rect> People);

    /// <summary>
    /// Результат выбора человека: вектор-эталон, визуальный кадр с рамкой, флаг успеха.
    /// </summary>
    public record PersonSelection(float[] Vector, Mat VisualFrame, bool Found);

    /// <summary>
    /// Сервис управления сессией работы с видео.
    /// Отвечает за: воспроизведение кадров, детекцию людей, выбор человека-эталона, анализ видео.
    /// Не зависит от UI — чистая бизнес-логика.
    /// </summary>
    public class VideoSessionService
    {
        private readonly VisionEngine _vision;
        private readonly VideoAnalyzerService _analyzer;

        /// <summary>
        /// Порог Евклидова расстояния для распознавания лиц (ArcFace).
        /// Векторы L2-нормализованы (512-мерные), поэтому расстояние ∈ [0, 2].
        /// 
        /// YOLO-Face детектирует лица, так что вектор извлекается с кадрированного лица.
        /// 
        /// Euclidean → Cosine Similarity:
        ///   0.6  → ~0.82 (очень строго — та же поза/свет)
        ///   1.0  → ~0.50 (стандарт для ArcFace — используется сейчас)
        ///   1.2  → ~0.39 (умеренный, смена ракурса/освещения)
        /// </summary>
        private const float SimilarityThreshold = 1.0f;

        public VideoSessionService(VisionEngine vision, VideoAnalyzerService analyzer)
        {
            _vision = vision;
            _analyzer = analyzer;
        }

        /// <summary>
        /// Открывает видео и возвращает метаданные (количество кадров, размер).
        /// </summary>
        public VideoInfo OpenVideo(string path)
        {
            using var capture = new VideoCapture(path);
            return new VideoInfo(
                TotalFrames: (int)capture.FrameCount,
                Width: (int)capture.FrameWidth,
                Height: (int)capture.FrameHeight
            );
        }

        /// <summary>
        /// Получает кадр по индексу с детекцией людей и Bitmap для UI.
        /// </summary>
        public FrameResult GetFrameWithDetection(string videoPath, int frameIndex)
        {
            using var capture = new VideoCapture(videoPath);
            capture.Set(OpenCvSharp.VideoCaptureProperties.PosFrames, frameIndex);

            var frame = new Mat();
            capture.Read(frame);

            if (frame.Empty())
                return new FrameResult(frame, new Bitmap(1, 1), new List<Rect>());

            // Детекция лиц
            var faces = _vision.DetectFaces(frame);

            // Рисуем рамки и создаём Bitmap для pictureBox
            Mat visualFrame = frame.Clone();

            foreach (var rect in faces)
            {
                Cv2.Rectangle(visualFrame, rect, Scalar.LimeGreen, 3);
            }

            var image = BitmapConverter.ToBitmap(visualFrame);

            return new FrameResult(frame, image, faces);
        }

        /// <summary>
        /// Выбирает человека по клику: находит рамку, извлекает embedding.
        /// </summary>
        public PersonSelection SelectPerson(Mat frame, List<Rect> people, int clickX, int clickY)
        {
            if (frame.Empty() || people.Count == 0)
                return new PersonSelection(Array.Empty<float>(), frame.Clone(), false);

            Mat finalFrame = frame.Clone();

            foreach (var rect in people)
            {
                // Рисуем все рамки зелёным
                Cv2.Rectangle(finalFrame, rect, Scalar.Green, 5);

                if (rect.Contains(new OpenCvSharp.Point(clickX, clickY)))
                {
                    // Ограничиваем рамку границами кадра
                    Rect clampedRect = rect;
                    clampedRect.X = Math.Max(0, clampedRect.X);
                    clampedRect.Y = Math.Max(0, clampedRect.Y);
                    clampedRect.Width = Math.Min(clampedRect.Width, frame.Width - clampedRect.X);
                    clampedRect.Height = Math.Min(clampedRect.Height, frame.Height - clampedRect.Y);

                    // Устанавливаем эталон
                    Mat faceCrop = new Mat(frame, clampedRect);
                    float[] vector = _vision.GetEmbedding(faceCrop);

                    // Рисуем выбранную рамку красным
                    Cv2.Rectangle(finalFrame, rect, Scalar.Red, 5);

                    return new PersonSelection(vector, finalFrame, true);
                }
            }

            return new PersonSelection(Array.Empty<float>(), finalFrame, false);
        }

        /// <summary>
        /// Анализирует видео: находит все сегменты с человеком-эталон.
        /// </summary>
        public List<VideoSegment>? Analyze(
            string videoPath,
            float[] referenceVector,
            Action<int, int, int>? progressCallback,
            CancellationToken cancelToken)
        {
            return _analyzer.Analyze(
                videoPath,
                referenceVector,
                SimilarityThreshold,
                progressCallback,
                cancelToken);
        }

        /// <summary>
        /// Получает FPS видеофайла.
        /// </summary>
        public double GetFPS(string videoPath)
        {
            return _analyzer.GetFPS(videoPath);
        }
    }
}