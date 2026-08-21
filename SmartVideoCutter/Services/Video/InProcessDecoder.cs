using System.IO;
using FFmpeg.AutoGen;
using OpenCvSharp;
using SmartVideoCutter.Models.Video;

namespace SmartVideoCutter.Services.Video;

/// Предикат отбора кадров: n — номер кадра от начала отрезка, tRelSec — время кадра в секундах
/// относительно startMs. true — кадр нужен анализатору (эквивалент ffmpeg select-фильтра).
public delegate bool FrameSelect(int n, double tRelSec);

/// <summary>
/// Внутрипроцессный декодер FFmpeg через FFmpeg.AutoGen (P/Invoke в нативные DLL, без запуска
/// ffmpeg.exe на отрезок). Один экземпляр = одна AVFormatContext: SeekAndReadFrames делает
/// av_seek_frame + flush и декодирует кадры до endMs — перемотка стоит миллисекунды против
/// ~400 мс «процесс + seek» у CLI. CUDA-декод (hw_device_ctx) при наличии GPU, иначе CPU;
/// кадры конвертируются sws_scale в BGR24 того же размера, что и exe-путь (rawvideo bgr24).
/// </summary>
public unsafe sealed class InProcessDecoder : IDisposable
{
    private AVFormatContext* _fmt;
    private AVCodecContext* _cctx;
    private AVPacket* _pkt;
    private AVFrame* _frame;
    private AVFrame* _sysFrame;
    private SwsContext* _sws;
    private int _streamIdx;

    private InProcessDecoder()
    {
    }

    /// Создаёт декодер или null — если открыть не удалось (fallback на exe-путь).
    public static InProcessDecoder? TryCreate(string videoPath, int width, int height)
    {
        try
        {
            // Путь к нативным DLL = папка ffmpeg.exe из настроек (там же лежат avcodec-61.dll и т.д.).
            var root = SettingsManager.CurrentSettings.FfmpegPath;
            if (!string.IsNullOrEmpty(root))
                ffmpeg.RootPath = root;

            var d = new InProcessDecoder();
            d.Open(videoPath, width, height);
            return d;
        }
        catch
        {
            // ошибка P/Invoke / отсутствия DLL — вызывающий переключится на ffmpeg.exe
            return null;
        }
    }

    private void Open(string videoPath, int width, int height)
    {
        AVFormatContext* fmt = null;
        if (ffmpeg.avformat_open_input(&fmt, videoPath, null, null) < 0)
            throw new IOException("avformat_open_input failed");
        _fmt = fmt;
        if (ffmpeg.avformat_find_stream_info(_fmt, null) < 0)
            throw new IOException("avformat_find_stream_info failed");

        for (uint i = 0; i < _fmt->nb_streams; i++)
            if (_fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                _streamIdx = (int)i;
                break;
            }

        if (_streamIdx < 0)
            throw new IOException("no video stream");

        var codecpar = _fmt->streams[_streamIdx]->codecpar;
        var decoder = ffmpeg.avcodec_find_decoder(codecpar->codec_id);
        if (decoder == null)
            throw new IOException($"no decoder for codec {codecpar->codec_id}");

        AVCodecContext* cctx = ffmpeg.avcodec_alloc_context3(decoder);
        if (cctx == null)
            throw new IOException("avcodec_alloc_context3 failed");
        _cctx = cctx;
        ffmpeg.avcodec_parameters_to_context(_cctx, codecpar);

        // CUDA-декод (эквивалент -hwaccel cuvid): только кодеки с GPU-поддержкой.
        if (IsCuvidCodec(codecpar->codec_id))
        {
            AVBufferRef* devBuf = null;
            int r = ffmpeg.av_hwdevice_ctx_create(&devBuf, AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, null, null, 0);
            if (r >= 0 && devBuf != null)
                _cctx->hw_device_ctx = devBuf; // иначе — CPU-декод того же кода
        }

        if (ffmpeg.avcodec_open2(_cctx, decoder, null) < 0)
            throw new IOException("avcodec_open2 failed");

        _pkt = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
    }

    /// Кадры отрезка [startMs, endMs) как Mat BGR24 (размер width x height), СТРИМИНГОМ:
    /// первый кадр отдаётся сразу после декода GOP-заголовка, а не после всего отрезка.
    /// Вызывающий обязан Dispose() каждый Mat; ранний выход (Dispose итератора) или отмена (ct)
    /// останавливают декод — иначе «ранний выход при обнаружении человека» в анализе бессмысленен.
    public IEnumerable<Mat> SeekAndReadFrames(double startMs, double endMs, int width, int height,
        CancellationToken ct, FrameSelect? select = null)
    {
        // Итератор не может быть unsafe (state-machine класс не наследует модификатор — CS0214),
        // поэтому цикл с указателями живёт в DecodeSegment на фоне, а кадры передаются через очередь.
        var queue = new System.Collections.Concurrent.BlockingCollection<Mat>(boundedCapacity: 8);
        Exception? error = null;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linkedCts.Token;

        var task = Task.Run(() =>
        {
            try
            {
                DecodeSegment(startMs, endMs, width, height, token, select, (mat, _) =>
                {
                    queue.Add(mat); // backpressure: не обгоняем потребителя бесконечно
                    return true;
                }, out _);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                queue.CompleteAdding();
            }
        });

        try
        {
            foreach (var mat in queue.GetConsumingEnumerable(token))
                yield return mat;
        }
        finally
        {
            // ранний выход / Dispose итератора — останавливаем фоновый декод
            linkedCts.Cancel();
            try
            {
                task.Wait(2000);
            }
            catch
            {
                /* ошибка уже в error */
            }

            queue.Dispose();
        }

        if (error is OperationCanceledException)
            throw new OperationCanceledException();
        if (error != null)
            throw error; // сбой seek/декода — вызывающий (ReadFrames) делает fallback на ffmpeg.exe
    }

    /// Декодирует отрезок [startMs, endMs) и для каждого отобранного кадра вызывает onFrame.
    /// false из onFrame или отмена — останавливают декод. eof = true, если чтение дошло до конца файла.
    private unsafe void DecodeSegment(double startMs, double endMs, int width, int height,
        CancellationToken ct, FrameSelect? select, Func<Mat, int, bool> onFrame, out bool eof)
    {
        var tb = _fmt->streams[_streamIdx]->time_base;
        // ВАЖНО: av_seek_frame со stream_index >= 0 принимает timestamp в единицах
        // time_base ПОТОКА (tb), а не AV_TIME_BASE. Иначе seekTs завышен в den/tb.den раз
        // (~12800x) и перемотка улетает к концу файла: все кадры отрезка имеют tRel < 0,
        // отбрасываются, и только первый отрезок (startMs = 0 → seekTs = 0) работает.
        long seekTs = tb.num > 0 ? (long)(startMs / 1000.0 * tb.den / tb.num) : 0;
        double endSec = (endMs - startMs) / 1000.0;

        // av_seek_frame(BACKWARD) ставит чтение на ключевой кадр НЕ ПОЗЖЕ startMs: кадры между ним
        // и startMs нужны декодеру для P-кадров, но не часть отрезка (тот же смысл, что у CLI -ss).
        if (ffmpeg.av_seek_frame(_fmt, _streamIdx, seekTs, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0)
            throw new IOException($"av_seek_frame failed at {startMs:F0} ms");
        ffmpeg.avcodec_flush_buffers(_cctx);

        int n = 0;
        eof = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int r = ffmpeg.av_read_frame(_fmt, _pkt);
                if (r < 0)
                {
                    eof = true; // конец файла / ошибка чтения
                    break;
                }

                if (_pkt->stream_index != _streamIdx)
                {
                    ffmpeg.av_packet_unref(_pkt);
                    continue;
                }

                ffmpeg.avcodec_send_packet(_cctx, _pkt);
                ffmpeg.av_packet_unref(_pkt);

                while (ffmpeg.avcodec_receive_frame(_cctx, _frame) == 0)
                {
                    double tRel = FrameTimeSec(tb, seekTs);
                    if (tRel < 0)
                        continue; // кадр раньше startMs: декодируем (зависимые P-кадры), но не отдаём
                    // в анализ и не считаем в n — как отбрасывание до -ss у ffmpeg.exe

                    if (tRel >= endSec)
                        return; // отрезок закончился — остаток не декодируем

                    bool keep = select?.Invoke(n, tRel) ?? true;
                    n++;
                    if (!keep)
                        continue;

                    var mat = ToBgr24(width, height);
                    if (mat == null)
                        continue; // кадр без данных — пропускаем

                    // SVC_DEBUG_FRAMES=<папка> — дамп отобранных кадров (n/tRel/абс. время),
                    // чтобы визуально проверить, что на инференс идут кадры именно этого отрезка.
                    var dumpDir = Environment.GetEnvironmentVariable("SVC_DEBUG_FRAMES");
                    if (!string.IsNullOrEmpty(dumpDir))
                    {
                        try
                        {
                            Directory.CreateDirectory(dumpDir);
                            Cv2.ImWrite(Path.Combine(dumpDir,
                                $"seg_{startMs:F0}_{endMs:F0}_n{n}_t{tRel:F3}.png"), mat);
                        }
                        catch
                        {
                            /* дамп — только для отладки */
                        }
                    }

                    if (!onFrame(mat, n))
                        return;
                }
            }
        }
        finally
        {
            // flush хвост декодера (дрейф n относительно ffmpeg select допустим: предикат по времени)
            while (ffmpeg.avcodec_receive_frame(_cctx, _frame) == 0)
            {
            }
        }
    }

    /// tRel кадра в секундах относительно seekTs (pts в единицах time_base). Отрицательное —
    /// кадр раньше startMs; вызывающий такие кадры пропускает.
    private double FrameTimeSec(AVRational tb, long seekTs)
    {
        if (_frame->pts == ffmpeg.AV_NOPTS_VALUE || tb.den <= 0)
            return double.MaxValue; // без pts — не знаем время; tail-предикат отработает по n
        return (double)(_frame->pts - seekTs) * tb.num / tb.den;
    }

    /// Текущий кадр → Mat BGR24 width x height (sws_scale), или null если кадр пустой.
    private Mat? ToBgr24(int width, int height)
    {
        AVFrame* src = _frame;
        if (src->hw_frames_ctx != null)
        {
            // GPU-кадр → системная память (эквивалент -hwaccel_output_format yuv420p)
            if (_sysFrame == null)
                _sysFrame = ffmpeg.av_frame_alloc();
            if (ffmpeg.av_hwframe_transfer_data(_sysFrame, src, 0) < 0)
                return null;
            src = _sysFrame;
        }

        int fmtId = src->format;
        if (fmtId == -1 || src->width <= 0 || src->height <= 0)
            return null;

        var sws = ffmpeg.sws_getCachedContext(_sws, src->width, src->height,
            (AVPixelFormat)fmtId, width, height, AVPixelFormat.AV_PIX_FMT_BGR24,
            ffmpeg.SWS_BILINEAR, null, null, null);
        if (sws == null)
            return null;
        _sws = sws;

        var mat = new Mat(height, width, MatType.CV_8UC3);
        unsafe
        {
            // sws_scale в AutoGen принимает управляемые массивы Byte*[] / Int32[]
            var srcSlice = new byte*[4];
            var srcStride = new int[4];
            for (uint i = 0; i < 4; i++)
            {
                srcSlice[i] = src->data[i];
                srcStride[i] = src->linesize[i];
            }

            byte* dstPtr = (byte*)mat.Data;
            var dstSlice = new byte*[1] { dstPtr };
            var dstStride = new int[1] { width * 3 };

            if (ffmpeg.sws_scale(sws, srcSlice, srcStride, 0, src->height, dstSlice, dstStride) <= 0)
                return null;
        }

        return mat;
    }

    /// Доступен ли CUDA-декод на этой машине (однократная проверка av_hwdevice_ctx_create).
    public static bool CudaDecodeAvailable()
    {
        if (_cudaProbe is bool cached)
            return cached;
        lock (_cudaLock)
        {
            if (_cudaProbe is bool c2)
                return c2;
            try
            {
                AVBufferRef* devBuf = null;
                int r = ffmpeg.av_hwdevice_ctx_create(&devBuf, AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, null, null, 0);
                _cudaProbe = r >= 0 && devBuf != null;
                if (devBuf != null)
                    ffmpeg.av_buffer_unref(&devBuf);
            }
            catch
            {
                _cudaProbe = false;
            }

            return _cudaProbe.Value;
        }
    }

    private static bool? _cudaProbe;
    private static readonly object _cudaLock = new();

    private static bool IsCuvidCodec(AVCodecID id) =>
        id is AVCodecID.AV_CODEC_ID_H264 or AVCodecID.AV_CODEC_ID_HEVC
            or AVCodecID.AV_CODEC_ID_AV1 or AVCodecID.AV_CODEC_ID_VP9;

    public void Dispose()
    {
        // локальные копии: &field напрямую не допускается (CS0212)
        if (_sysFrame != null)
        {
            AVFrame* p = _sysFrame;
            ffmpeg.av_frame_free(&p);
        }

        if (_frame != null)
        {
            AVFrame* p = _frame;
            ffmpeg.av_frame_free(&p);
        }

        if (_pkt != null)
        {
            AVPacket* p = _pkt;
            ffmpeg.av_packet_free(&p);
        }

        if (_sws != null)
        {
            SwsContext* p = _sws;
            ffmpeg.sws_freeContext(p);
        }

        if (_cctx != null)
        {
            AVCodecContext* p = _cctx;
            ffmpeg.avcodec_free_context(&p);
        }

        if (_fmt != null)
        {
            AVFormatContext* p = _fmt;
            ffmpeg.avformat_close_input(&p);
        }
    }
}