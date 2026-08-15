using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using SmartVideoCutterVLC.Models;

namespace SmartVideoCutterVLC.Services;

public class FFmpegService
{
    public static VideoInfo GetVideoInfo(string videoPath)
    {
        var ffprobePath = Path.Combine(SettingsManager.CurrentSettings.FfmpegPath, "ffprobe.exe");

        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-v"); // уровень детализации логов
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-select_streams"); // выбор конкретных потоков для анализа
        psi.ArgumentList.Add("v:0"); // первое видео
        psi.ArgumentList.Add("-show_entries"); // секция данных и ее поля
        psi.ArgumentList.Add("stream=r_frame_rate,width,height"); // format | packet | frame
        psi.ArgumentList.Add("-of"); // output format
        psi.ArgumentList.Add("json"); // csv=p=0 | json
        psi.ArgumentList.Add(videoPath);

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var doc = JsonDocument.Parse(process.StandardOutput.ReadToEnd());
        process.WaitForExit();

        string frameRateFraction = doc.RootElement.GetProperty("streams")[0].GetProperty("r_frame_rate").GetString();
        string[] parts = frameRateFraction.Split('/');
        double fps = double.Parse(parts[0]) / double.Parse(parts[1]);

        int width = doc.RootElement.GetProperty("streams")[0].GetProperty("width").GetInt32();
        int height = doc.RootElement.GetProperty("streams")[0].GetProperty("height").GetInt32();

        return new VideoInfo { Fps = fps, Width = width, Height = height };
    }

    public static List<Keyframe> GetVideoKeyframes(string videoPath)
    {
        var ffprobePath = Path.Combine(SettingsManager.CurrentSettings.FfmpegPath, "ffprobe.exe");

        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-v"); // уровень детализации логов
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-select_streams"); // выбор конкретных потоков для анализа
        psi.ArgumentList.Add("v:0"); // первое видео
        psi.ArgumentList.Add("-show_entries"); // секция данных и ее поля
        psi.ArgumentList.Add("packet=pts_time,flags"); // format | packet | frame
        //psi.ArgumentList.Add("-skip_frame nokey"); // получить только ключевые кадры (не работает в packet)
        psi.ArgumentList.Add("-of"); // output format
        psi.ArgumentList.Add("json"); // csv=p=0 | json
        psi.ArgumentList.Add(videoPath);

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var stream = process.StandardOutput.BaseStream;
        using var doc = JsonDocument.Parse(stream);
        process.WaitForExit();

        var keyframes = new List<Keyframe>();

        if (doc.RootElement.TryGetProperty("packets", out var framesArray) &&
            framesArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var frame in framesArray.EnumerateArray())
            {
                if (frame.TryGetProperty("flags", out var flags))
                {
                    if (!flags.ToString().StartsWith('K'))
                        continue;
                }

                if (frame.TryGetProperty("pts_time", out var ptsTimeString) &&
                    double.TryParse(ptsTimeString.GetString(), CultureInfo.InvariantCulture, out double ptsTime))
                {
                    keyframes.Add(new Keyframe { Timestamp = ptsTime });
                }
            }
        }

        return keyframes;
    }
}