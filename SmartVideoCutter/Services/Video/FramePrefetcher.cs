using System.Collections.Concurrent;
using OpenCvSharp;

namespace SmartVideoCutter.Services.Video;

/// <summary>
/// Prefetch кадров отрезка [startMs, endMs): фоновый поток декодирует их (FrameReader.ReadFrames)
/// и буферизует в ограниченную очередь. Анализ (инференс)
/// не ждёт запуска процесса и декода первого кадра: следующий отрезок стартует параллельно
/// с анализом текущего, и время запуска ffmpeg перекрывается работой ONNX.
/// Dispose() убивает процесс ffmpeg и освобождает буферизованные кадры; ранний выход из
/// цикла анализа (человек найден) - Dispose - остаток отрезка не декодируется (ранний выход сохранён).
/// </summary>
public sealed class FramePrefetcher : IDisposable
{
    /// Максимум кадров в буфере: поглощает задержку запуска и всплески декода.
    /// 1080p BGR ~ 6 МБ/кадр -> ~50 МБ на prefetcher.
    private const int MaxBufferedFrames = 8;

    private readonly BlockingCollection<Mat> _queue = new(MaxBufferedFrames);
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenRegistration _outerReg;
    private readonly Task _worker;
    private Exception? _error; // ошибка потока декода (если была) - бросается в TakeNext

    public FramePrefetcher(string videoPath, double startMs, double endMs, int width, int height, CancellationToken ct,
        string? selectFilter = null)
    {
        _outerReg = ct.Register(_cts.Cancel); // внешняя отмена -> убийство этого ffmpeg
        _worker = Task.Run(() =>
            Pump(FrameReader.ReadFrames(videoPath, startMs, endMs, width, height, _cts.Token, selectFilter)));
    }

    /// Следующий кадр или null - конец отрезка. Бросает исключение, если декодирование завершилось ошибкой.
    public Mat? TakeNext()
    {
        if (_queue.TryTake(out var mat, Timeout.Infinite))
            return mat;
        if (_error != null)
            throw _error;
        return null;
    }

    /// Фоновый поток: качает кадры из ffmpeg в очередь. Add блокируется при полной очереди -
    /// backpressure на ffmpeg (процесс не обгоняет анализатор бесконечно).
    private void Pump(IEnumerable<Mat> frames)
    {
        try
        {
            using var e = frames.GetEnumerator(); // Dispose итератора убивает процесс ffmpeg
            while (e.MoveNext())
                _queue.Add(e.Current);
        }
        catch (InvalidOperationException)
        {
            /* очередь завершена из Dispose - не ошибка */
        }
        catch (OperationCanceledException)
        {
            // отмена — штатный сценарий, НЕ ошибка: глотаем, чтобы _worker не fault'ился.
            // Иначе необработанный OCE задачи ретраивался финализатором
            // (UnobservedTaskException → «The operation was canceled»).
        }
        catch (Exception ex)
        {
            _error = ex;
        }
        finally
        {
            _queue.CompleteAdding();
        }
    }

    public void Dispose()
    {
        // Отмена убивает ffmpeg даже если поток декода заблокирован в MoveNext (ожидание кадра).
        _cts.Cancel();
        try
        {
            _worker.Wait(2000);
        }
        catch
        {
            /* ошибка потока уже сохранена в _error */
        }

        while (_queue.TryTake(out var m))
            m.Dispose(); // освобождаем необработанные кадры
        _queue.Dispose();
        _outerReg.Dispose();
        _cts.Dispose();
    }
}