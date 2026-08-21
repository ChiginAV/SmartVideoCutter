using System.Globalization;
using System.IO;

namespace SmartVideoCutter.Services.Video;

/// <summary>
/// Экспорт выбранных отрезков без перекодирования видео: нарезка сегментов (видео stream copy,
/// аудио re-encode), склейка concat и финальная пересборка индекса перемотки.
/// </summary>
public static class VideoExporter
{
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
            RebuildIndex(outputPath, ct);
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

            // concat demuxer: все сегменты из одного источника → -c copy допустим.
            // -fflags +genpts (до -i): генерация PTS на входе — если у сегментов различались
            //   начальные таймкоды, concat не оставляет «дыр» во времени на стыках.
            // -avoid_negative_ts make_zero: сдвигает всю шкалу так, чтобы первый пакет был в 0 —
            //   монотонно растущие таймкоды критичны для MKV (иначе ломается индекс перемотки).
            var listFile = Path.Combine(tempDir, "concat.txt");
            File.WriteAllLines(listFile, files.Select(f => $"file '{f}'"));

            FFmpegProcess.Run(new[]
            {
                "-y", "-f", "concat", "-safe", "0", "-fflags", "+genpts", "-i", listFile,
                "-c", "copy", "-avoid_negative_ts", "make_zero", outputPath
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

        // Пересборка индекса перемотки (Cues для MKV / stss для MP4 и т.д.) чистым stream-copy ремуксом:
        // ffmpeg не умеет пересчитать индекс «на месте», единственный способ — re-mux с -c copy.
        RebuildIndex(outputPath, ct);
    }

    /// <summary>
    /// Пересчёт индекса перемотки у готового файла (Cues для MKV, moov/stss-таблицы для MP4 и т.д.).
    /// У ffmpeg нет команды для «in-place» пересборки индекса — единственный способ это re-mux
    /// (stream copy, без перекодирования: демуксер помечает ключевые блоки, муксер строит
    /// индекс заново). Зачем: при склейке concat + -c copy ffmpeg может записать неполный/битый
    /// индекс — плеер (MPC-BE) без него ищет ключевые кадры перебором кластеров → медленная
    /// перемотка, тогда как у исходника с нормальным индексом она мгновенная. Ремукс просто
    /// перечитывает и перезаписывает пакеты — быстро (скорость диска), качество не меняется.
    /// </summary>
    private static void RebuildIndex(string outputPath, CancellationToken ct)
    {
        string tmp = outputPath + ".reindex.tmp";
        // MP4-семейство: faststart переносит moov (с таблицами индекса stss/stsz) в начало файла —
        // без него плеер сначала читает хвост, чтобы даже начать перемотку.
        bool isMp4Family = Path.GetExtension(outputPath).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov";
        try
        {
            FFmpegProcess.Run(isMp4Family
                ? new[] { "-y", "-i", outputPath, "-c", "copy", "-movflags", "+faststart", tmp }
                : new[] { "-y", "-i", outputPath, "-c", "copy", tmp },
                "пересчитать индекс", ct);
            File.Move(tmp, outputPath, overwrite: true);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Постобработка: исходный файл уже записан и playable — не превращаем успешный
            // экспорт в ошибку из-за срыва пересборки индекса.
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { /* не критично */ }
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

    /// Один отрезок [startMs, endMs): видео — stream copy (границы отрезков — ключевые кадры,
    /// перемотка точная), аудио — перекодирование. Причины и фиксы A/V рассинхрона:
    /// - с чистым -c copy ffmpeg отбрасывает все аудио-пакеты до точки нарезки → звук начинает
    ///   идти на ~23 мс (длина AAC-кадра) позже видео. Перекодирование хотя бы одного потока
    ///   включает у ffmpeg accurate_seek: аудио режется точно в startMs и выравнивается с видео.
    /// - -shortest: длина copy-видео и перекодированного аудио может различаться на ~50 мс
    ///   (priming/padding AAC-кодера, -t обрезает потоки по-разному). Без этого рассогласование
    ///   накапливается на каждой склейке concat-ом → «периодический» сдвиг звука в итоговом файле.
    /// -avoid_negative_ts make_zero — нормализация таймкодов copy-потока.
    private static void CopyRange(string videoPath, string outputPath, double startMs, double endMs,
        CancellationToken ct)
    {
        // Аудио-кодек под контейнер результата: AAC не поддерживается в AVI → туда MP3.
        var ext = Path.GetExtension(outputPath).ToLowerInvariant();
        string audioCodec = ext == ".avi" ? "libmp3lame" : "aac";

        // -accurate_seek (по умолчанию включён, указан явно): точная нарезка по startMs для
        // обоих потоков. Работает только при перекодировании хотя бы одного потока — поэтому
        // ниже аудио всегда перекодируется; с чистым -c copy ffmpeg режет видео по I-кадру и
        // отбрасывает аудио до точки нарезки → рассинхрон ~23 мс (длина AAC-кадра) на старте.
        FFmpegProcess.Run(new[]
            {
                "-y", "-ss", (startMs / 1000.0).ToString(CultureInfo.InvariantCulture),
                "-accurate_seek",
                "-i", videoPath,
                "-t", ((endMs - startMs) / 1000.0).ToString(CultureInfo.InvariantCulture),
                "-c:v", "copy", "-c:a", audioCodec,
                "-shortest", // длина видео и аудио совпадает точно — нет накопления сдвига на склейках
                "-avoid_negative_ts", "make_zero", outputPath
            }, $"извлечь отрезок {startMs:0}–{endMs:0} мс", ct);
    }
}
