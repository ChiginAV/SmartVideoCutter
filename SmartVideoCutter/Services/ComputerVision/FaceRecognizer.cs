using System.Globalization;
using System.Runtime.InteropServices;
using FaceAiSharp;
using FaceAiSharp.Extensions;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SmartVideoCutter.Models;
using SmartVideoCutter.Models.Video;
using SmartVideoCutter.Services.Video;

namespace SmartVideoCutter.Services.ComputerVision;

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

    /// Окно «хвоста»: последние 0.2 c перед следующим ключевым кадром (≈ 5 кадров при 25 fps).
    private const double TailWindowSec = 0.2;

    /// Анализирует каждый отрезок [keyframe[i], keyframe[i+1]) выбранным алгоритмом,
    /// выставляя Keyframe.IsSelected. onSegmentAnalyzed(i + 1) вызывается после каждого отрезка.
    /// Оба алгоритма — один проход по кадрам (RunSegments); select-фильтр ffmpeg пропускает
    /// через pipe только анализируемые кадры, prefetch перекрывает запуск процесса инференсом.
    public void Analyze(string videoPath, IReadOnlyList<Keyframe> keyframes, double durationMs,
        int width, int height, double fps, float[] reference, AppAnalysisAlgorithm algorithm,
        Action<int> onSegmentAnalyzed, CancellationToken ct)
    {
        switch (algorithm)
        {
            case AppAnalysisAlgorithm.ThreeBetweenKeyframes:
                // «Быстрый»: 3 кадра на отрезок — ключевой, средний и «хвост». Полный декод
                // отрезка неизбежен (последний кадр зависит от всего GOP), но через pipe
                // передаются только эти кадры.
                RunSegments(videoPath, keyframes, durationMs, width, height, reference,
                    i => SelectExprFast(keyframes, i, durationMs, fps), onSegmentAnalyzed, ct);
                break;
            default: // ThreePerSecond
            {
                // «Точный»: инференс каждый step-й кадр (step = floor(fps/3), минимум 1;
                // fps неизвестен → 1) + «хвост».
                int step = fps > 0 ? Math.Max(1, (int)(fps / 3.0)) : 1;
                RunSegments(videoPath, keyframes, durationMs, width, height, reference,
                    i => SelectExprPrecise(keyframes, i, durationMs, step), onSegmentAnalyzed, ct);
                break;
            }
        }
    }

    /// select-фильтр «быстрого» алгоритма: ключевой (первый кадр отрезка), средний (окно в 1 кадр
    /// вокруг середины по времени; fps неизвестен → пропускаем) и «хвост». n — номер кадра от
    /// начала отрезка, t — время кадра в секундах от начала отрезка (-ss перед -i сбрасывает pts).
    private static string SelectExprFast(IReadOnlyList<Keyframe> keyframes, int i, double durationMs, double fps)
    {
        // Длительность отрезка (t в select — относительно начала отрезка).
        double segLen =
            (((i + 1 < keyframes.Count) ? keyframes[i + 1].Timestamp : durationMs) - keyframes[i].Timestamp) / 1000.0;
        var parts = new List<string> { "eq(n,0)" }; // ключевой: первый кадр отрезка
        if (fps > 0)
        {
            double mid = segLen / 2.0;
            parts.Add(
                $"between(t,{mid.ToString(CultureInfo.InvariantCulture)},{(mid + 1.0 / fps).ToString(CultureInfo.InvariantCulture)})");
        }

        parts.Add(
            $"gte(t,{(segLen - TailWindowSec).ToString(CultureInfo.InvariantCulture)})"); // «хвост»: не зависит от fps
        return string.Join("+", parts);
    }

    /// select-фильтр «точного» алгоритма: каждый step-й кадр + «хвост». null — фильтр не нужен.
    private static string? SelectExprPrecise(IReadOnlyList<Keyframe> keyframes, int i, double durationMs, int step)
    {
        if (step <= 1)
            return null; // все кадры («хвост» уже включён)

        double segLen =
            (((i + 1 < keyframes.Count) ? keyframes[i + 1].Timestamp : durationMs) - keyframes[i].Timestamp) / 1000.0;
        return $"mod(n,{step})==0+gte(t,{(segLen - TailWindowSec).ToString(CultureInfo.InvariantCulture)})";
    }

    /// Проход по отрезкам для «точного» алгоритма. selectExpr(i) — выражение select-фильтра
    /// (n — номер кадра от начала отрезка), null — без фильтра; каждый кадр из pipe отправляется
    /// в инференс, ранний выход при обнаружении человека.
    private void RunSegments(string videoPath, IReadOnlyList<Keyframe> keyframes, double durationMs,
        int width, int height, float[] reference, Func<int, string?> selectExpr,
        Action<int> onSegmentAnalyzed, CancellationToken ct)
    {
        FramePrefetcher? cur = null; // декодер текущего отрезка
        FramePrefetcher? next = null; // декодер отрезка i+1 (разогрет параллельно с анализом i)

        // SVC_DEBUG_TIMING=1 — per-segment тайминги в bench.log (диагностика скорости анализа).
        var timingLog = Environment.GetEnvironmentVariable("SVC_DEBUG_TIMING") == "1";
        double maxSegMs = 0;
        int maxSegIdx = -1;

        try
        {
            for (int i = 0; i < keyframes.Count; i++)
            {
                if (ct.IsCancellationRequested)
                    break;
                var kf = keyframes[i];
                double endMs = (i + 1 < keyframes.Count) ? keyframes[i + 1].Timestamp : durationMs;

                // Декодер текущего отрезка: разогретый prefetcher или новый.
                cur = next;
                next = null;
                cur ??= new FramePrefetcher(videoPath, kf.Timestamp, endMs, width, height, ct, selectExpr(i));

                // Стартуем декодирование СЛЕДУЮЩЕГО отрезка ДО анализа текущего:
                // запуск ffmpeg.exe и seek перекрываются работой ONNX.
                if (i + 1 < keyframes.Count)
                    next = new FramePrefetcher(videoPath, keyframes[i + 1].Timestamp,
                        (i + 2 < keyframes.Count) ? keyframes[i + 2].Timestamp : durationMs, width, height, ct,
                        selectExpr(i + 1));

                var swSeg = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    kf.IsSelected = AnalyzeSegment(cur, reference, ct);
                    onSegmentAnalyzed(i + 1);
                }
                finally
                {
                    swSeg.Stop();
                    if (timingLog && swSeg.Elapsed.TotalMilliseconds > maxSegMs)
                    {
                        maxSegMs = swSeg.Elapsed.TotalMilliseconds;
                        maxSegIdx = i + 1;
                    }

                    // Dispose убивает ffmpeg текущего отрезка: при раннем выходе (человек найден)
                    // или исключении остаток не декодируется — ранний выход сохранён.
                    cur.Dispose();
                    cur = null;
                }
            }
        }
        finally
        {
            cur?.Dispose(); // исключение до анализа отрезка (создан новый prefetcher)
            next?.Dispose(); // отмена/конец: разогретый, но не использованный prefetcher

            if (timingLog && maxSegIdx >= 0)
            {
                try
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(AppContext.BaseDirectory, "bench.log"),
                        $"[TIMING] медленный отрезок: #{maxSegIdx} — {maxSegMs:F0} мс" + Environment.NewLine);
                }
                catch
                {
                    /* не критично */
                }
            }
        }
    }

    /// Анализ одного отрезка из потока кадров prefetcher'а: инференс на каждом кадре из pipe
    /// (select-фильтр уже отобрал нужные, включая «хвост»), ранний выход при обнаружении человека.
    private bool AnalyzeSegment(FramePrefetcher frames, float[] reference, CancellationToken ct)
    {
        while (true)
        {
            if (ct.IsCancellationRequested)
                break;
            var frame = frames.TakeNext();
            if (frame == null)
                break; // конец отрезка
            try
            {
                if (ContainsPerson(frame, reference))
                    return true;
            }
            finally
            {
                frame.Dispose();
            }
        }

        return false;
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