using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace SmartVideoCutter.Services.Video;

/// <summary>
/// Последовательное декодирование кадров отрезка [startMs, endMs): сначала пробует
/// внутрипроцессный декодер (пул InProcessDecoder, seek без запуска процесса), при любой
/// неудаче — классический путь через ffmpeg.exe. Контракт: вызывающий Dispose() каждый Mat;
/// ранний выход или отмена (ct) останавливают декодирование.
/// </summary>
public static class FrameReader
{
    // ===== Внутрипроцессный декодер (FFmpeg.AutoGen) — пул на видео =====
    // Один AVFormatContext переиспользуется между отрезками: seek = av_seek_frame (~мс) вместо
    // ~400 мс «запуск ffmpeg.exe + -ss». Пул: до 2 prefetcher'а работают параллельно (текущий
    // отрезок анализируется, следующий уже декодируется). SVC_FFMPEG_INPROC=0 — выключить.

    private static readonly object _decLock = new();
    private static readonly ConcurrentQueue<InProcessDecoder> _decPool = new();
    private static string? _decVideoPath;
    private static bool _inprocDisabled; // DLL нет / P/Invoke сломан — больше не пробуем

    public static bool InProcEnabled() =>
        Environment.GetEnvironmentVariable("SVC_FFMPEG_INPROC") != "0";

    internal static bool InProcDisabled => _inprocDisabled;

    private static InProcessDecoder? AcquireDecoder(string videoPath, int width, int height)
    {
        if (!InProcEnabled() || _inprocDisabled)
            return null;
        lock (_decLock)
        {
            // видео сменилось — старые контексты уже не нужны
            if (_decVideoPath != videoPath)
            {
                while (_decPool.TryDequeue(out var old))
                    old.Dispose();
                _decVideoPath = videoPath;
            }

            if (_decPool.TryDequeue(out var d))
                return d;

            var created = InProcessDecoder.TryCreate(videoPath, width, height);
            if (created == null)
                _inprocDisabled = true; // не повторяем неудачные попытки на каждый отрезок
            return created;
        }
    }

    private static void ReleaseDecoder(InProcessDecoder d)
    {
        lock (_decLock)
        {
            if (InProcEnabled() && !_inprocDisabled)
                _decPool.Enqueue(d);
            else
                d.Dispose();
        }
    }

    /// Освобождает пул декодеров (вызывается по завершении анализа — контексты больше не нужны).
    public static void ReleaseDecoders()
    {
        lock (_decLock)
        {
            while (_decPool.TryDequeue(out var d))
                d.Dispose();
            _decVideoPath = null;
        }
    }

    /// Выражение ffmpeg select-фильтра → делегат отбора (n — номер кадра от начала отрезка,
    /// tRelSec — время кадра в секундах от начала). Понимает ровно те выражения, что генерирует
    /// FaceRecognizer: eq(n,X) / mod(n,S)==0 / between(t,A,B) / gte(t,T), объединённые «+» (OR).
    /// Неизвестный синтаксис → null (вызывающий идёт в exe-путь с оригинальным фильтром).
    private static FrameSelect? ParseSelect(string expr)
    {
        var orParts = expr.Split('+');
        int eqN = -1, modS = 0;
        double betweenA = 0, betweenB = 0, gteT = double.MaxValue;
        bool hasBetween = false, hasGte = false;

        foreach (var raw in orParts)
        {
            var p = raw.Trim();
            if (p.StartsWith("eq(n,", StringComparison.Ordinal))
                eqN = int.Parse(p[5..^1], CultureInfo.InvariantCulture);
            else if (p.StartsWith("mod(n,", StringComparison.Ordinal))
                modS = int.Parse(p[6..].Replace(")==0", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
            else if (p.StartsWith("between(t,", StringComparison.Ordinal))
            {
                var ab = p[10..^1].Split(',');
                betweenA = double.Parse(ab[0], CultureInfo.InvariantCulture);
                betweenB = double.Parse(ab[1], CultureInfo.InvariantCulture);
                hasBetween = true;
            }
            else if (p.StartsWith("gte(t,", StringComparison.Ordinal))
            {
                gteT = double.Parse(p[6..^1], CultureInfo.InvariantCulture);
                hasGte = true;
            }
            else
                return null; // неизвестная форма — не рискуем, exe-путь разберётся сам
        }

        bool betweenDone = false; // «средний» кадр: один (окно ≈ 1 кадр)
        return (n, tRel) =>
            (eqN >= 0 && n == eqN) ||
            (modS > 0 && n % modS == 0) ||
            (hasBetween && !betweenDone && tRel >= betweenA && tRel < betweenB && (betweenDone = true)) ||
            (hasGte && tRel >= gteT);
    }

    public static IEnumerable<Mat> ReadFrames(string videoPath, double startMs, double endMs,
        int width, int height, CancellationToken ct, string? selectFilter = null)
    {
        FrameSelect? select = selectFilter != null ? ParseSelect(selectFilter) : null; // null → exe-путь

        var dec = AcquireDecoder(videoPath, width, height);
        if (dec != null && select != null)
        {
            // yield нельзя оборачивать try/catch (CS1626), поэтому «пробный» первый MoveNext
            // (именно он выполняет av_seek_frame и может упасть) делается в этом обычном методе:
            // сбой seek → fallback на ffmpeg.exe. Ошибки посреди отрезка редки (повреждённый файл)
            // и уйдут как ошибка анализа — exe-путь на них бы тоже не спас.
            var inner = dec.SeekAndReadFrames(startMs, endMs, width, height, ct, select).GetEnumerator();
            try
            {
                bool hasFirst = inner.MoveNext();
                return InProcStream(dec, inner, hasFirst);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _inprocDisabled = true; // сбой P/Invoke-пути — дальше только ffmpeg.exe
                inner.Dispose();
                ReleaseDecoder(dec);
            }
        }

        return ReadFramesViaProcess(videoPath, startMs, endMs, width, height, ct, selectFilter);
    }

    /// Поток кадров из внутрипроцессного декодера; Dispose итератора возвращает декодер в пул.
    private static IEnumerable<Mat> InProcStream(InProcessDecoder dec, IEnumerator<Mat> e, bool hasFirst)
    {
        try
        {
            if (hasFirst)
                yield return e.Current;
            while (e.MoveNext())
                yield return e.Current;
        }
        finally
        {
            e.Dispose();
            ReleaseDecoder(dec);
        }
    }

    /// Классический путь: последовательное декодирование кадров отрезка [startMs, endMs) одним
    /// процессом ffmpeg.
    /// -ss перед -i: быстрый seek к ключевому кадру не позже startMs; т.к. startMs — сам ключевой
    /// кадр, перемотка точна, и кадры декодируются строго по порядку.
    /// selectFilter (выражение фильтра select, n — номер кадра от начала отрезка): из pipe
    /// передаются только совпавшие кадры — остальные отбрасываются внутри ffmpeg до передачи,
    /// что снижает объём данных и аллокации Mat в step раз.
    /// -threads N: с prefetch'ом параллельно работают 2 процесса; по умолчанию (все ядра)
    /// они бы перегружали CPU. N задаётся переменной окружения SVC_FFMPEG_THREADS
    /// (0/пусто — без флага, дефолт ffmpeg), значение по умолчанию 2.
    private static IEnumerable<Mat> ReadFramesViaProcess(string videoPath, double startMs, double endMs,
        int width, int height, CancellationToken ct, string? selectFilter = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FFmpegProcess.ExePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true, // stderr → память (нет спама в консоли, deadlock исключён)
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error"); // только ошибки
        int threads = 2; // ограничение потоков декода (см. комментарий выше)
        if (int.TryParse(Environment.GetEnvironmentVariable("SVC_FFMPEG_THREADS"), out var t))
            threads = t;
        if (threads > 0)
        {
            psi.ArgumentList.Add("-threads");
            psi.ArgumentList.Add(threads.ToString(CultureInfo.InvariantCulture));
        }

        // Аппаратный декод NVIDIA CUVID: -hwaccel — входной параметр (до -i).
        // -hwaccel_output_format yuv420p: кадры сразу скачиваются в системную память,
        // поэтому select-фильтр и BGR24-конверсия работают на CPU как обычно.
        // Значение "system" в ffmpeg 7.1 невалидно ("Unrecognised hwaccel output format") —
        // процесс молча работает без GPU; yuv420p — проверенно рабочее значение.
        // Без этого флага кадры остаются в CUDA-формате и filter graph ломается (auto_scale).
        if (FFmpegProbe.GetHwDecoder(videoPath) != null && FFmpegProbe.HwAccelWorks(videoPath))
        {
            psi.ArgumentList.Add("-hwaccel");
            psi.ArgumentList.Add("cuvid");
            psi.ArgumentList.Add("-hwaccel_output_format");
            psi.ArgumentList.Add("yuv420p");
        }

        psi.ArgumentList.Add("-ss"); // быстрый seek: startMs — ключевой кадр, перемотка точная
        psi.ArgumentList.Add((startMs / 1000.0).ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(((endMs - startMs) / 1000.0).ToString(CultureInfo.InvariantCulture));
        if (selectFilter != null)
        {
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add($"select='{selectFilter}'");
        }

        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("bgr24");
        psi.ArgumentList.Add("pipe:1");

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stderr = FFmpegProcess.AttachStderrCapture(process);
        using var reg = ct.Register(() =>
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
                /* процесс мог уже завершиться */
            }
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
                    break; // конец потока (или ранний сбой ffmpeg — проверяем ниже)

                var mat = new Mat(height, width, MatType.CV_8UC3);
                Marshal.Copy(buffer, 0, mat.Data, size);
                yield return mat;
            }

            // Pipe закрыт не по отмене: если ffmpeg завершился с ошибкой (например,
            // cuvid-декод не запустился) — бросаем, а не молча возвращаем урезанный поток.
            if (!ct.IsCancellationRequested)
            {
                process.WaitForExit(2000);
                if (process.ExitCode != 0)
                {
                    string errText;
                    lock (stderr) errText = stderr.ToString();
                    throw new IOException(
                        $"FFmpeg: ошибка декодирования (код {process.ExitCode}). ffmpeg: {FFmpegProcess.Tail(errText, 500)}");
                }
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
}
