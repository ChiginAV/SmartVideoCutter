using System.Globalization;
using System.IO;
using System.Text;

namespace SmartVideoCutter.Services.Video;

/// <summary>
/// Запуск ffmpeg.exe как процесса: единая точка запуска со stderr-capture (без deadlock),
/// отменой через CancellationToken и форматированием ошибок. Используется экспортёром
/// и пробами (ProbeHwAccel).
/// </summary>
internal static class FFmpegProcess
{
    /// Путь к ffmpeg.exe из настроек.
    public static string ExePath => Path.Combine(SettingsManager.CurrentSettings.FfmpegPath, "ffmpeg.exe");

    /// Запуск ffmpeg.exe.
    /// stderr редиректится и опустошается фоном в память (CaptureStderrAsync, 9.9) —
    /// pipe не заполняется, deadlock (9.6) исключён, консоль не получает вывод ffmpeg.
    /// Отмена: по срабатыванию ct процесс убивается, после чего бросается OperationCanceledException.
    public static void Run(IReadOnlyList<string> args, string what, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            RedirectStandardError = true, // 9.9.3: stderr → память — прогрессные строки не в консоль
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-hide_banner"); // 9.9.3
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error"); // только ошибки
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        // SVC_DEBUG_EXPORT=1 — полная команда в bench.log (диагностика экспорта).
        if (Environment.GetEnvironmentVariable("SVC_DEBUG_EXPORT") == "1")
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "bench.log"),
                    "[EXPORT] " + string.Join(' ', psi.ArgumentList) + Environment.NewLine);
            }
            catch
            {
                /* не критично */
            }
        }

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stderr = AttachStderrCapture(process); // фоновое опустошение stderr-pipe (иначе deadlock, см. 9.6)
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
    public static StringBuilder AttachStderrCapture(Process process)
    {
        var sb = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                lock (sb)
                    sb.AppendLine(e.Data);
        };
        process.BeginErrorReadLine();
        return sb;
    }

    /// Хвост текста для сообщений об ошибках (9.9).
    public static string Tail(string text, int max)
    {
        text = text.Trim();
        if (text.Length == 0)
            return "(пусто)";
        return text.Length <= max ? text : "..." + text[^max..];
    }
}
