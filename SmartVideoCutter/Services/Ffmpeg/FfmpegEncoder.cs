using System.Diagnostics;

namespace SmartVideoCutter.Services.Ffmpeg;

/// Нарезка и склейка видео через ffmpeg (Fast Forward MPEG)
/// Префикс FF означает «fast forward» (быстрая перемотка вперед), а MPEG отсылает к экспертной группе Moving Picture Experts Group
public class FfmpegEncoder
{
    public const string Path = "E:/Soft/Video/ffmpeg-7.1.1/ffmpeg.exe";

    private readonly string _path;

    public FfmpegEncoder() : this(Path)
    {
    }

    public FfmpegEncoder(string path) => _path = path;

    /// <summary>Нарезает сегмент видео (синхронно).</summary>
    public void CutSegment(string inputPath, string outputPath, int startFrame, int endFrame, double fps)
    {
        double startTime = startFrame / fps;
        double endTime = endFrame / fps;

        string args = $"-ss {startTime} -to {endTime} -i \"{inputPath}\" -c copy \"{outputPath}\"";

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = _path,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true
        });

        process?.WaitForExit();
    }

    /// <summary>Нарезает сегмент видео (асинхронно).</summary>
    public async Task CutSegmentAsync(string inputPath, string outputPath, int startFrame, int endFrame, double fps,
        CancellationToken cancelToken = default)
    {
        double startTime = startFrame / fps;
        double endTime = endFrame / fps;

        string args = $"-ss {startTime} -to {endTime} -i \"{inputPath}\" -c copy \"{outputPath}\"";

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = _path,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
        });

        if (process == null)
            throw new InvalidOperationException("Не удалось запустить ffmpeg.");

        var stderrTask = process.StandardError.ReadToEndAsync();
        var tcs = new TaskCompletionSource<bool>();
        process.EnableRaisingEvents = true;
        process.Exited += (s, e) => tcs.TrySetResult(true);

        cancelToken.Register(() =>
        {
            if (!process.HasExited)
                process.Kill();
        });

        await tcs.Task;
        string stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"ffmpeg CutSegment ошибка (exit code {process.ExitCode}):\n{stderr.Trim()}");
    }

    /// <summary>Склеивает сегменты (синхронно).</summary>
    public void JoinSegments(string listFilePath, string finalOutputPath)
    {
        string args = $"-f concat -safe 0 -i \"{listFilePath}\" -c copy \"{finalOutputPath}\"";

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = _path,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true
        });

        process?.WaitForExit();
    }

    /// <summary>Склеивает сегменты (асинхронно).</summary>
    public async Task JoinSegmentsAsync(string listFilePath, string finalOutputPath,
        CancellationToken cancelToken = default)
    {
        string args = $"-f concat -safe 0 -i \"{listFilePath}\" -c copy \"{finalOutputPath}\"";

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = _path,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
        });

        if (process == null)
            throw new InvalidOperationException("Не удалось запустить ffmpeg.");

        var stderrTask = process.StandardError.ReadToEndAsync();
        var tcs = new TaskCompletionSource<bool>();
        process.EnableRaisingEvents = true;
        process.Exited += (s, e) => tcs.TrySetResult(true);

        cancelToken.Register(() =>
        {
            if (!process.HasExited)
                process.Kill();
        });

        await tcs.Task;
        string stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"ffmpeg JoinSegments ошибка (exit code {process.ExitCode}):\n{stderr.Trim()}");
    }
}