using OpenCvSharp;
using SmartVideoCutter.Models;

namespace SmartVideoCutter.Services
{
    /// <summary>
    /// Сервис анализа видео: находит сегменты с целевым человеком
    /// по интервалам между ключевыми кадрами.
    /// </summary>
    public class VideoAnalyzerService
    {
        private readonly VideoProcessor _videoProcessor;
        private readonly VisionEngine _vision;

        public VideoAnalyzerService(VideoProcessor videoProcessor, VisionEngine vision)
        {
            _videoProcessor = videoProcessor;
            _vision = vision;
        }

        /// <summary>
        /// Анализирует видео по интервалам между ключевыми кадрами.
        /// Если в интервале найден человек, весь интервал считается подходящим сегментом.
        /// </summary>
        public List<VideoSegment> Analyze(
            string videoPath,
            float[] referenceVector,
            float similarityThreshold,
            Action<int, int, int> progressCallback,
            CancellationToken cancelToken)
        {
            if (string.IsNullOrEmpty(videoPath))
                throw new ArgumentException("Путь к видео не указан.", nameof(videoPath));

            if (referenceVector == null || referenceVector.Length == 0)
                throw new ArgumentException("Вектор-эталон не установлен.", nameof(referenceVector));

            // Получаем FPS
            double fps = _videoProcessor.GetFPS(videoPath);

            // Фаза 1: сигнал UI — начало получения ключевых кадров (total=0)
            progressCallback?.Invoke(0, 0, 0);

            // Проверка отмены перед получением ключевых кадров
            cancelToken.ThrowIfCancellationRequested();

            // Получаем ключевые кадры БЫСТРЫМ способом (packet-level, без декодирования)
            List<int> keyFrames = _videoProcessor.GetKeyFrames(videoPath, fps, cancelToken);
            var segments = new List<VideoSegment>();

            if (keyFrames.Count < 2)
            {
                throw new Exception(
                    $"В видео найдено только {keyFrames.Count} ключевых кадров. Необходимо минимум 2 для анализа.");
            }

            // Сигнал UI: переход к фазе 2 — анализ видео
            int totalIntervals = keyFrames.Count - 1;
            progressCallback?.Invoke(0, totalIntervals, 0);

            using var capture = new VideoCapture(videoPath);
            int analysisStep = (int)Math.Max(1, fps); // Проверяем 1 кадр в секунду внутри интервала

            Mat frame = new Mat();

            for (int i = 0; i < totalIntervals; i++)
            {
                cancelToken.ThrowIfCancellationRequested();

                int intervalStart = keyFrames[i];
                int intervalEnd = keyFrames[i + 1];
                bool foundInInterval = false;

                // Проверяем кадры в этом интервале с шагом ~1 кадр/сек
                for (int f = intervalStart; f < intervalEnd && !foundInInterval; f += analysisStep)
                {
                    cancelToken.ThrowIfCancellationRequested();

                    capture.Set(VideoCaptureProperties.PosFrames, f);
                    if (capture.Read(frame) && frame.Empty() == false)
                    {
                        var detectedFaces = _vision.DetectFaces(frame);

                        if (CheckIfPersonIsPresent(frame, detectedFaces, referenceVector, similarityThreshold))
                        {
                            foundInInterval = true;
                            segments.Add(new VideoSegment
                            {
                                StartFrame = intervalStart,
                                EndFrame = intervalEnd
                            });
                        }
                    }
                }

                // Передаём current, total и количество найденных сегментов
                progressCallback?.Invoke(i + 1, totalIntervals, segments.Count);
            }

            return segments;
        }

        /// <summary>
        /// Возвращает FPS видео.
        /// </summary>
        public double GetFPS(string videoPath)
        {
            return _videoProcessor.GetFPS(videoPath);
        }

        /// <summary>
        /// Проверяет, присутствует ли целевой человек в кадре среди обнаруженных людей.
        /// </summary>
        private bool CheckIfPersonIsPresent(
            Mat frame,
            List<Rect> people,
            float[] referenceVector,
            float similarityThreshold)
        {
            foreach (var rect in people)
            {
                Mat personCrop = new Mat(frame, rect);
                float[] currentVector = _vision.GetEmbedding(personCrop);
                double distance = FaceSimilarityCalculator.EuclideanDistance(referenceVector, currentVector);

                if (distance < similarityThreshold)
                    return true;
            }

            return false;
        }
    }
}