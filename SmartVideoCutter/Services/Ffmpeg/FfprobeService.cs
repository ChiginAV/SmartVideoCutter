using System.Diagnostics;
using System.Globalization;
using OpenCvSharp;

namespace SmartVideoCutter.Services.Ffmpeg;

/// Чтение метаданных видео через ffprobe: ключевые кадры (packet-level и frame-level).
public class FfprobeService
{
    public const string Path = "E:/Soft/Video/ffmpeg-7.1.1/ffprobe.exe";

    private readonly string _path;

    public FfprobeService() : this(Path) { }

    // Для тестов / кастомных путей
    public FfprobeService(string path) => _path = path;

    /// Получает ключевые кадры БЫСТРО через packet-level анализ (без декодирования).
    public List<int> GetKeyFrames(string videoPath, double fps, CancellationToken cancelToken = default)
    {
        List<int> keyFrames = new List<int>();

        // Packet-level: чтение флагов пакетов из контейнера без декодирования
        string args = $"-v error -select_streams v:0 -show_entries packet=pts_time,flags -of csv=p=0 \"{videoPath}\"";

        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = _path,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);

        // Читаем построчно для поддержки отмены
        using var reader = process.StandardOutput;
        while (!reader.EndOfStream)
        {
            cancelToken.ThrowIfCancellationRequested();

            string? line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            string trimmed = line.Trim();
            int commaPos = trimmed.IndexOf(',');
            if (commaPos <= 0)
                continue;

            string ptsTimeStr = trimmed.Substring(0, commaPos).Trim();
            string flagsStr = trimmed.Substring(commaPos + 1).Trim();

            // 'K' в flags означает keyframe
            if (!flagsStr.StartsWith("K"))
                continue;

            if (double.TryParse(ptsTimeStr, CultureInfo.InvariantCulture, out double ptsTime))
            {
                int frameIndex = (int)Math.Round(ptsTime * fps);
                keyFrames.Add(frameIndex);
            }
        }

        process.WaitForExit();

        // Добавляем 0-й кадр если его нет
        if (keyFrames.Count == 0 || keyFrames[0] > 0)
        {
            keyFrames.Insert(0, 0);
        }

        return keyFrames;
    }

    /// Получает ключевые кадры через покадровое декодирование (МЕДЛЕННО — точный).
    public List<int> GetKeyFramesByFrames(string videoPath, double fps, int totalFrames = 0, Action<int>? progressCallback = null)
    {
        List<int> keyFrames = new List<int>();

            // Используем frame-level информацию: key_frame=1 для I-кадров (требует декодирования)
            string args = $"-v error -select_streams v:0 -show_entries frame=pts_time,key_frame -of csv=p=0 \"{videoPath}\"";

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = _path,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);

            // Читаем построчно для поддержки прогресса в реальном времени
            using var reader = process.StandardOutput;
            int frameCount = 0;

            while (!reader.EndOfStream)
            {
                string? line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                frameCount++;

                // Вызываем коллбек прогресса
                progressCallback?.Invoke(frameCount);

                // Разделяем по запятой: формат "pts_time,key_frame"
                string[] parts = line.Split(',');
                if (parts.Length < 2)
                    continue;

                // Формат: pts_time,key_frame (первое поле — время, второе — флаг key_frame)
                string ptsTimeStr = parts[0].Trim();
                string keyFrameStr = parts[1].Trim();

                // key_frame=1 означает I-кадр (ключевой кадр)
                if (keyFrameStr == "1" && double.TryParse(ptsTimeStr, CultureInfo.InvariantCulture, out double ptsTime))
                {
                    int frameIndex = (int)Math.Round(ptsTime * fps);
                    keyFrames.Add(frameIndex);
                }
            }

            process.WaitForExit();

            // Добавляем 0-й кадр если его нет
            if (keyFrames.Count == 0 || keyFrames[0] > 0)
            {
                keyFrames.Insert(0, 0);
            }

            return keyFrames;
    }

    /// Возвращает FPS видео (через OpenCV VideoCapture).
    public double GetFPS(string videoPath)
    {
        using var capture = new VideoCapture(videoPath);
        return capture.Fps;
    }
}