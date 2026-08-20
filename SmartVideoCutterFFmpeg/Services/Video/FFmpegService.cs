using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using OpenCvSharp;


namespace SmartVideoCutterFFmpeg.Services.Video;

public class FFmpegService
{
    public static List<Keyframe> GetVideoKeyframes(string videoPath)
    {
        using var doc = GetInfoFromFfprobe(videoPath, "packet=pts_time,flags");

        var keyframes = new List<Keyframe>();

        if (doc.RootElement.TryGetProperty("packets", out var framesArray) &&
            framesArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var frame in framesArray.EnumerateArray())
            {
                if (frame.TryGetProperty("flags", out var flags))
                {
                    if (!flags.ToString().Contains('K'))
                        continue;
                }

                if (frame.TryGetProperty("pts_time", out var ptsTimeString) &&
                    double.TryParse(ptsTimeString.GetString(), CultureInfo.InvariantCulture, out double ptsTime))
                {
                    keyframes.Add(new Keyframe
                    {
                        Index = keyframes.Count + 1,
                        Timestamp = ptsTime * 1000 // переводим секунды в миллисекунды
                    });
                }
            }
        }

        return keyframes;
    }

    private static JsonDocument GetInfoFromFfprobe(string videoPath, string entries)
    {
        var ffprobePath = Path.Combine(SettingsManager.CurrentSettings.FfmpegPath, "ffprobe.exe");

        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true, // stderr НЕ редиректим: он не нужен, а незакрытый stderr-pipe = deadlock (см. 9.6)
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-v"); // уровень детализации логов
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-select_streams"); // выбор конкретных потоков для анализа
        psi.ArgumentList.Add("v:0"); // первое видео
        psi.ArgumentList.Add("-show_entries"); // секция данных и ее поля
        psi.ArgumentList.Add(entries); // format | packet | frame
        psi.ArgumentList.Add("-of"); // output format
        psi.ArgumentList.Add("json"); // csv=p=0 | json
        psi.ArgumentList.Add(videoPath);
        // "-skip_frame nokey" (получить только ключевые кадры) не работает в packet

        using var process = new Process { StartInfo = psi };
        process.Start();

        // читаем весь stdout до WaitForExit — иначе возможен deadlock при полном буфере
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        return JsonDocument.Parse(output);
    }

    /// Один кадр в BGR по таймстампу (для распознавания).
    public static Mat ExtractFrame(string videoPath, long timestampMs, int width, int height)
    {
        var ffmpegPath = Path.Combine(SettingsManager.CurrentSettings.FfmpegPath, "ffmpeg.exe");

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true, // 9.9.2: stderr → память (CaptureStderrAsync) — нет спама в консоли, deadlock исключён
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-hide_banner"); // 9.9.2: без баннера и build config
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error"); // только ошибки
        psi.ArgumentList.Add("-ss"); // быстрый seek: от ближайшего keyframe не позже таймстампа
        psi.ArgumentList.Add((timestampMs / 1000.0).ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("bgr24");
        psi.ArgumentList.Add("pipe:1");

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stderr = AttachStderrCapture(process); // фоновое опустошение stderr-pipe (иначе deadlock, см. 9.6)

        int size = width * height * 3;
        var buffer = new byte[size];
        int read = 0;
        while (read < size)
        {
            int n = process.StandardOutput.BaseStream.Read(buffer, read, size - read);
            if (n <= 0)
                break;
            read += n;
        }

        process.WaitForExit();
        string errText;
        lock (stderr) errText = stderr.ToString();

        if (read < size)
            throw new IOException($"Не удалось прочитать кадр на {timestampMs} мс (прочитано {read} из {size} байт). ffmpeg: {Tail(errText, 500)}");

        // Mat(rows, cols, type, byte[]) — internal в OpenCvSharp4, а SetArray(byte[]) — только CV_8UC1.
        // Копируем байты BGR в нативный буфер Mat: Marshal.Copy — статический метод,
        // mat.Data (nint) неявно преобразуется в IntPtr, unsafe не нужен.
        var mat = new Mat(height, width, MatType.CV_8UC3);
        Marshal.Copy(buffer, 0, mat.Data, buffer.Length);
        return mat;
    }

    /// Последовательное декодирование всех кадров отрезка [startMs, endMs) одним процессом ffmpeg.
    /// -ss перед -i: быстрый seek к ключевому кадру не позже startMs; т.к. startMs — сам ключевой
    /// кадр, перемотка точна, и кадры декодируются строго по порядку.
    /// Вызывающий обязан Dispose() возвращаемые Mat. Ранний выход из цикла (Dispose итератора)
    /// или отмена (ct) убивают процесс ffmpeg.
    public static IEnumerable<Mat> ReadFrames(string videoPath, double startMs, double endMs,
        int width, int height, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(SettingsManager.CurrentSettings.FfmpegPath, "ffmpeg.exe"),
            RedirectStandardOutput = true,
            RedirectStandardError = true, // stderr → память (нет спама в консоли, deadlock исключён)
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error"); // только ошибки
        psi.ArgumentList.Add("-ss"); // быстрый seek: startMs — ключевой кадр, перемотка точная
        psi.ArgumentList.Add((startMs / 1000.0).ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(((endMs - startMs) / 1000.0).ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("bgr24");
        psi.ArgumentList.Add("pipe:1");

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stderr = AttachStderrCapture(process);
        using var reg = ct.Register(() =>
        {
            try { process.Kill(true); } catch { /* процесс мог уже завершиться */ }
        });

        int size = width * height * 3;
        var buffer = new byte[size];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = 0;
                while (read < size)
                {
                    int n;
                    try
                    {
                        n = process.StandardOutput.BaseStream.Read(buffer, read, size - read);
                    }
                    catch (IOException)
                    {
                        break; // процесс убит (отмена) — pipe разорван
                    }
                    if (n <= 0)
                        break;
                    read += n;
                }
                if (read < size)
                    break; // конец потока

                var mat = new Mat(height, width, MatType.CV_8UC3);
                Marshal.Copy(buffer, 0, mat.Data, size);
                yield return mat;
            }
        }
        finally
        {
            // ранний выход (вызывающий dispose'нул итератор) или отмена — убиваем процесс
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
                process.WaitForExit(2000);
            }
            catch
            {
                /* процесс мог уже завершиться */
            }
        }
    }

    /// Экспорт отрезков без перекодирования. ranges — [startMs, endMs), отсортированы, не пересекаются.
    /// Экспорт отрезков без перекодирования. ranges — [startMs, endMs), отсортированы, не пересекаются.
    /// Отрезки для экспорта: от выбранного ключевого кадра до следующего ключевого
    /// (или до конца видео). Соседние выбранные кадры сливаются в один непрерывный отрезок.
    public static List<(double StartMs, double EndMs)> BuildSegments(IReadOnlyList<Keyframe> keyframes,
        double durationMs)
    {
        var segments = new List<(double, double)>();
        for (int i = 0; i < keyframes.Count; i++)
        {
            if (!keyframes[i].IsSelected)
                continue;
            int j = i;
            while (j + 1 < keyframes.Count && keyframes[j + 1].IsSelected)
                j++;
            double end = (j + 1 < keyframes.Count) ? keyframes[j + 1].Timestamp : durationMs;
            segments.Add((keyframes[i].Timestamp, end));
            i = j;
        }

        return segments;
    }

    public static void ExportSegments(string videoPath, string outputPath,
        IReadOnlyList<(double StartMs, double EndMs)> ranges, IProgress<int> progress, CancellationToken ct = default)
    {
        if (ranges.Count == 1)
        {
            CopyRange(videoPath, outputPath, ranges[0].StartMs, ranges[0].EndMs, ct);
            progress.Report(1);
            return;
        }

        string ext = Path.GetExtension(videoPath);
        var tempDir = Path.Combine(Path.GetTempPath(), "SmartVideoCutter_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var files = new List<string>();
            for (int i = 0; i < ranges.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = Path.Combine(tempDir, $"seg_{i:000}{ext}");
                CopyRange(videoPath, file, ranges[i].StartMs, ranges[i].EndMs, ct);
                files.Add(file);
                progress.Report(i + 1);
            }

            // concat demuxer: все сегменты из одного источника → -c copy допустим
            var listFile = Path.Combine(tempDir, "concat.txt");
            File.WriteAllLines(listFile, files.Select(f => $"file '{f}'"));

            RunFfmpeg(new[]
            {
                "-y", "-f", "concat", "-safe", "0", "-i", listFile,
                "-c", "copy", outputPath
            }, "склеить отрезки", ct);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                /* временные файлы — не критично */
            }
        }
    }

    /// <summary>
    /// Имя = оригинал + суффикс; при коллизии — «имя (1).ext», «имя (2).ext», … (конвенция Windows).
    /// </summary>
    public static string GetUniqueFileName(string originalPath, string suffix)
    {
        string dir = Path.GetDirectoryName(originalPath) ?? ".";
        string name = Path.GetFileNameWithoutExtension(originalPath) + suffix;
        string ext = Path.GetExtension(originalPath);

        string candidate = Path.Combine(dir, name + ext);
        int n = 1;
        while (File.Exists(candidate))
            candidate = Path.Combine(dir, $"{name} ({n++}){ext}");
        return candidate;
    }

    /// Один отрезок [startMs, endMs) без перекодирования.
    /// -ss перед -i + -c copy: ffmpeg перемотает ровно на ключевой кадр — наши границы и есть ключевые кадры.
    private static void CopyRange(string videoPath, string outputPath, double startMs, double endMs, CancellationToken ct)
    {
        RunFfmpeg(new[]
        {
            "-y", "-ss", (startMs / 1000.0).ToString(CultureInfo.InvariantCulture),
            "-i", videoPath,
            "-t", ((endMs - startMs) / 1000.0).ToString(CultureInfo.InvariantCulture),
            "-c", "copy", outputPath
        }, $"извлечь отрезок {startMs:0}–{endMs:0} мс", ct);
    }

    /// Запуск ffmpeg.exe.
    /// stderr редиректится и опустошается фоном в память (CaptureStderrAsync, 9.9) —
    /// pipe не заполняется, deadlock (9.6) исключён, консоль не получает вывод ffmpeg.
    /// Отмена: по срабатыванию ct процесс убивается, после чего бросается OperationCanceledException.
    private static void RunFfmpeg(IReadOnlyList<string> args, string what, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(SettingsManager.CurrentSettings.FfmpegPath, "ffmpeg.exe"),
            RedirectStandardError = true, // 9.9.3: stderr → память — прогрессные строки не в консоль
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-hide_banner"); // 9.9.3
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error"); // только ошибки
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stderr = AttachStderrCapture(process); // фоновое опустошение stderr-pipe (иначе deadlock, см. 9.6)
        using var reg = ct.Register(() =>
        {
            try { process.Kill(true); } catch { /* процесс мог уже завершиться */ }
        });
        process.WaitForExit();
        string errText;
        lock (stderr) errText = stderr.ToString();
        if (ct.IsCancellationRequested)
            throw new OperationCanceledException();
        if (process.ExitCode != 0)
            throw new IOException($"FFmpeg: не удалось {what} (код {process.ExitCode}). ffmpeg: {Tail(errText, 500)}");
    }

    /// Фоновое чтение stderr процесса в память (9.9): опустошение pipe исключает deadlock (9.6),
    /// консоль не получает вывод ffmpeg. Вызывающий читает StringBuilder после WaitForExit.
    /// Событийный BeginErrorReadLine (а не Task.Run) — без дженериков/inference, канонический паттерн .NET.
    private static StringBuilder AttachStderrCapture(Process process)
    {
        var sb = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                lock (sb) sb.AppendLine(e.Data);
        };
        process.BeginErrorReadLine();
        return sb;
    }

    /// Хвост текста для сообщений об ошибках (9.9).
    private static string Tail(string text, int max)
    {
        text = text.Trim();
        if (text.Length == 0)
            return "(пусто)";
        return text.Length <= max ? text : "..." + text[^max..];
    }
}