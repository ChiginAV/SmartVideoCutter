using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SmartVideoCutter.Services.Video;

/// <summary>
/// ffprobe-запросы и разведка возможностей декодирования: ключевые кадры, кодек видео,
/// аппаратный декод NVIDIA CUVID (проверка «работает ли на этой машине»).
/// </summary>
public static class FFmpegProbe
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

    /// Кодеки, поддерживаемые аппаратным декодом NVIDIA CUVID.
    private static readonly HashSet<string> CuvidCodecs = new() { "h264", "hevc", "av1", "vp9" };

    /// Кэш «путь → кодек» (ffprobe один раз на видео).
    private static readonly Dictionary<string, string?> _codecCache = new();

    /// Имя декодера CUVID для видео (например "h264_cuvid") или null — декодировать на CPU.
    /// Отключение: переменная окружения SVC_FFMPEG_HWACCEL=0.
    public static string? GetHwDecoder(string videoPath)
    {
        if (Environment.GetEnvironmentVariable("SVC_FFMPEG_HWACCEL") == "0")
            return null;

        string? codec;
        lock (_codecCache)
        {
            if (!_codecCache.TryGetValue(videoPath, out codec))
            {
                codec = DetectCodec(videoPath);
                _codecCache[videoPath] = codec;
            }
        }

        return codec != null && CuvidCodecs.Contains(codec) ? codec + "_cuvid" : null;
    }

    /// Однократный результат проверки «cuvid реально работает на этой машине» (GPU + драйверы + сборка).
    private static bool? _hwAccelProbeResult;

    private static readonly object _hwAccelLock = new();

    /// Работает ли аппаратный декод cuvid на этой машине. Проверяем один раз (прогоняем ffmpeg
    /// с -hwaccel cuvid на 0.2 c видео): без NVIDIA/драйверов или в сборке без cuvid это упадёт,
    /// и тогда все последующие отрезки пойдут через CPU-декод.
    public static bool HwAccelWorks(string videoPath)
    {
        lock (_hwAccelLock)
        {
            if (_hwAccelProbeResult is bool cached)
                return cached;
            _hwAccelProbeResult = ProbeHwAccel(videoPath);
            return _hwAccelProbeResult.Value;
        }
    }

    /// Пробный запуск: декод 0.2 c через cuvid в null-вывод. true — exit code 0 (cuvid работает).
    private static bool ProbeHwAccel(string videoPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FFmpegProcess.ExePath,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-hwaccel");
            psi.ArgumentList.Add("cuvid");
            psi.ArgumentList.Add("-hwaccel_output_format");
            psi.ArgumentList.Add("yuv420p");
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(videoPath);
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add("0.2");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("null");
            psi.ArgumentList.Add("-");

            using var process = new Process { StartInfo = psi };
            process.Start();
            // stderr опустошаем асинхронно — иначе при полном буфере ReadToEnd/WaitForExit в deadlock.
            var sb = new StringBuilder();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    lock (sb)
                        sb.AppendLine(e.Data);
            };
            process.BeginErrorReadLine();
            process.WaitForExit(10000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false; // не удалось запустить — CPU-декод
        }
    }

    /// Кодек первого видео-потока (ffprobe). null при ошибке — тогда CPU-декод.
    public static string? DetectCodec(string videoPath)
    {
        try
        {
            using var doc = GetInfoFromFfprobe(videoPath, "stream=codec_name");
            if (doc.RootElement.TryGetProperty("streams", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var s in arr.EnumerateArray())
                    if (s.TryGetProperty("codec_name", out var c))
                        return c.GetString();
        }
        catch
        {
            /* ffprobe не удалось — CPU-декод */
        }

        return null;
    }

    /// Описание декодирования для окна прогресса: GPU/CPU и путь (внутрипроцессный / ffmpeg.exe).
    public static string DescribeDecoding(string videoPath)
    {
        if (FrameReader.InProcEnabled() && !FrameReader.InProcDisabled)
        {
            bool gpu = DetectCodec(videoPath) is string c && CuvidCodecs.Contains(c) && InProcessDecoder.CudaDecodeAvailable();
            return gpu ? "декод: GPU (CUDA, внутрипроцессный)" : "декод: CPU (внутрипроцессный)";
        }

        if (GetHwDecoder(videoPath) != null && HwAccelWorks(videoPath))
            return "декод: GPU (cuvid, ffmpeg.exe)";
        return "декод: CPU (ffmpeg.exe)";
    }

    private static JsonDocument GetInfoFromFfprobe(string videoPath, string entries)
    {
        var ffprobePath = Path.Combine(SettingsManager.CurrentSettings.FfmpegPath, "ffprobe.exe");

        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput =
                true, // stderr НЕ редиректим: он не нужен, а незакрытый stderr-pipe = deadlock (см. 9.6)
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
}
