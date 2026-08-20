using System.Runtime.InteropServices;
using FaceAiSharp;
using FaceAiSharp.Extensions;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace SmartVideoCutterFFmpeg.Services.ComputerVision;

/// Распознавание лиц (ArcFace) через FaceAiSharp + поиск человека в видео.
/// InferenceSession не потокобезопасен — сериализуем вызовы lock'ом.
public sealed class FaceRecognizer : IDisposable
{
    /// Порог косинусного сходства «тот же человек» (dot произведение нормализованных embedding'ов).
    private const double PersonMatchThreshold = 0.42;

    private readonly IFaceEmbeddingsGenerator _embeddings;
    private readonly FaceDetector _detector;
    private readonly object _lock = new();

    public FaceRecognizer(FaceDetector detector)
    {
        _detector = detector;

        var options = new SessionOptions();
        if (OrtEnv.Instance().GetAvailableProviders().Contains("DmlExecutionProvider"))
            options.AppendExecutionProvider_DML();
        // иначе CPU (дефолт)

        _embeddings = FaceAiSharpBundleFactory.CreateFaceEmbeddingsGenerator(options);
    }

    /// Embedding выбранного лица: кроп с margin + аффинное выравнивание по landmarks.
    public float[] GenerateReferenceEmbedding(Mat frame, OpenCvSharp.Rect boxPx, IReadOnlyList<PointF> landmarks)
    {
        lock (_lock)
        {
            var (crop, cx, cy) = CropWithMargin(frame, boxPx);
            try
            {
                using var rgb = new Mat();
                Cv2.CvtColor(crop, rgb, ColorConversionCodes.BGR2RGB);
                int bytes = rgb.Width * rgb.Height * 3;

                ReadOnlySpan<Rgb24> pixels;
                unsafe
                {
                    // zero-copy: span по нативному буферу Mat (img освобождается раньше rgb)
                    pixels = MemoryMarshal.Cast<byte, Rgb24>(new Span<byte>((byte*)rgb.Data, bytes));
                }

                using var img = Image.LoadPixelData<Rgb24>(pixels, rgb.Width, rgb.Height);

                // landmarks в координатах кадра → координаты кропа
                var local = landmarks
                    .Select(p => new PointF(p.X - cx, p.Y - cy))
                    .ToArray();

                // AlignFaceUsingLandmarks мутирует in-place; img — одноразовая копия
                ArcFaceEmbeddingsGenerator.AlignFaceUsingLandmarks(img, local);
                return _embeddings.GenerateEmbedding(img);
            }
            finally
            {
                crop.Dispose();
            }
        }
    }

    /// Есть ли в кадре искомый человек: детекция + embedding + порог сходства.
    public bool ContainsPerson(Mat frame, float[] reference) =>
        _detector.Detect(frame).Any(f =>
            f.Landmarks != null &&
            GenerateReferenceEmbedding(frame, f.BoxPx, f.Landmarks).Dot(reference) >= PersonMatchThreshold);

    /// Анализирует каждый отрезок [keyframe[i], keyframe[i+1]) выбранным алгоритмом,
    /// выставляя Keyframe.IsSelected. onSegmentAnalyzed(i + 1) вызывается после каждого отрезка.
    public void Analyze(string videoPath, IReadOnlyList<Keyframe> keyframes, double durationMs,
        int width, int height, double fps, float[] reference, AppAnalysisAlgorithm algorithm,
        Action<int> onSegmentAnalyzed, CancellationToken ct)
    {
        switch (algorithm)
        {
            case AppAnalysisAlgorithm.ThreeBetweenKeyframes:
                AnalyzeThreeBetweenKeyframes(videoPath, keyframes, durationMs, width, height, fps,
                    reference, onSegmentAnalyzed, ct);
                break;
            default: // ThreePerSecond
                AnalyzeThreePerSecond(videoPath, keyframes, durationMs, width, height, fps,
                    reference, onSegmentAnalyzed, ct);
                break;
        }
    }

    /// «Точный» алгоритм: инференс каждый step-й кадр (step = floor(fps/3), минимум 1;
    /// fps неизвестен → 1); первый кадр отрезка — ключевой, всегда анализируется.
    /// Не нашли человека — дополнительно анализируем последний кадр отрезка («хвост» —
    /// до step-1 кадра перед следующим ключевым кадром).
    private void AnalyzeThreePerSecond(string videoPath, IReadOnlyList<Keyframe> keyframes,
        double durationMs, int w, int h, double fps, float[] reference,
        Action<int> onSegmentAnalyzed, CancellationToken ct)
    {
        int step = fps > 0 ? Math.Max(1, (int)(fps / 3.0)) : 1;

        for (int i = 0; i < keyframes.Count; i++)
        {
            if (ct.IsCancellationRequested)
                break;
            var kf = keyframes[i];
            double endMs = (i + 1 < keyframes.Count) ? keyframes[i + 1].Timestamp : durationMs;

            // Последовательное декодирование всех кадров отрезка одним процессом ffmpeg.
            // using IEnumerator: при раннем выходе (человек найден) Dispose() убивает процесс.
            using var frames = FFmpegService.ReadFrames(videoPath, kf.Timestamp, endMs, w, h, ct).GetEnumerator();
            bool found = false;
            Mat? lastFrame = null; // последний декодированный кадр отрезка (финальная проверка)
            int frameIndex = 0;
            try
            {
                while (!found && frames.MoveNext())
                {
                    if (ct.IsCancellationRequested)
                        break;
                    var frame = frames.Current;
                    if (frameIndex % step == 0)
                        found = ContainsPerson(frame, reference);
                    if (found)
                    {
                        frame.Dispose();
                        break;
                    }
                    lastFrame?.Dispose();
                    lastFrame = frame;
                    frameIndex++;
                }

                // Финальная проверка последнего кадра отрезка (если он ещё не анализировался):
                // «хвост» (до step-1 кадра) перед следующим ключевым кадром.
                int lastIndex = frameIndex - 1;
                bool lastAnalyzed = lastIndex % step == 0;
                if (!found && !ct.IsCancellationRequested && lastFrame != null && !lastAnalyzed)
                    found = ContainsPerson(lastFrame, reference);
            }
            finally
            {
                lastFrame?.Dispose();
            }

            kf.IsSelected = found;
            onSegmentAnalyzed(i + 1);
        }
    }

    /// «Быстрый» алгоритм: ровно 3 кадра на отрезок — ключевой (первый), средний
    /// (индекс оценивается по fps; fps неизвестен → пропускаем) и последний перед
    /// следующим ключевым кадром.
    private void AnalyzeThreeBetweenKeyframes(string videoPath, IReadOnlyList<Keyframe> keyframes,
        double durationMs, int w, int h, double fps, float[] reference,
        Action<int> onSegmentAnalyzed, CancellationToken ct)
    {
        for (int i = 0; i < keyframes.Count; i++)
        {
            if (ct.IsCancellationRequested)
                break;
            var kf = keyframes[i];
            double endMs = (i + 1 < keyframes.Count) ? keyframes[i + 1].Timestamp : durationMs;

            // Индекс среднего кадра (оценка по fps; fps неизвестен → пропускаем).
            long estFrames = fps > 0 ? Math.Max(1, (long)((endMs - kf.Timestamp) / 1000.0 * fps)) : 0;
            int midIndex = estFrames > 0 ? (int)(estFrames / 2) : -1;

            // Последовательное декодирование всех кадров отрезка одним процессом ffmpeg.
            // using IEnumerator: при раннем выходе (человек найден) Dispose() убивает процесс.
            using var frames = FFmpegService.ReadFrames(videoPath, kf.Timestamp, endMs, w, h, ct).GetEnumerator();
            bool found = false;
            Mat? lastFrame = null; // последний декодированный кадр отрезка (финальная проверка)
            int frameIndex = 0;
            try
            {
                while (!found && frames.MoveNext())
                {
                    if (ct.IsCancellationRequested)
                        break;
                    var frame = frames.Current;
                    if (frameIndex == 0 || frameIndex == midIndex)
                        found = ContainsPerson(frame, reference);
                    if (found)
                    {
                        frame.Dispose();
                        break;
                    }
                    lastFrame?.Dispose();
                    lastFrame = frame;
                    frameIndex++;
                }

                // Финальная проверка последнего кадра отрезка (если он ещё не анализировался).
                int lastIndex = frameIndex - 1;
                bool lastAnalyzed = lastIndex == 0 || lastIndex == midIndex;
                if (!found && !ct.IsCancellationRequested && lastFrame != null && !lastAnalyzed)
                    found = ContainsPerson(lastFrame, reference);
            }
            finally
            {
                lastFrame?.Dispose();
            }

            kf.IsSelected = found;
            onSegmentAnalyzed(i + 1);
        }
    }

    /// Кроп с margin (аффинному преобразованию нужен контекст вокруг лица).
    private static (Mat Crop, int X, int Y) CropWithMargin(Mat frame, OpenCvSharp.Rect box, double margin = 0.35)
    {
        int mx = (int)(box.Width * margin), my = (int)(box.Height * margin);
        int x = Math.Max(0, box.X - mx);
        int y = Math.Max(0, box.Y - my);
        int w = Math.Min(frame.Width - x, box.Width + 2 * mx);
        int h = Math.Min(frame.Height - y, box.Height + 2 * my);
        return (new Mat(frame, new OpenCvSharp.Rect(x, y, w, h)).Clone(), x, y);
    }

    public void Dispose()
    {
        if (_embeddings is IDisposable e)
            e.Dispose();
    }
}
