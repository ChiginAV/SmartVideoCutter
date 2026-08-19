using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FFMediaToolkit;
using FFMediaToolkit.Decoding;
using FFMediaToolkit.Graphics;
using OpenCvSharp;

namespace SmartVideoCutterFFmpeg.Services.Video;

/// <summary>
/// Состояние плеера (аналог Flyleaf Player.Status).
/// </summary>
public enum PlayerStatus
{
    Stopped,
    Playing,
    Paused,
    Failed
}

/// <summary>
/// Простой видео-плеер БЕЗ звука: FFMediaToolkit (декод) + BitmapSource (рендер).
/// Поверхность близка к Flyleaf: Status, CurTime, SeekTo, события
/// (PropertyChanged, SeekCompleted, PlaybackStopped) и GetCurrentFrame() для FaceAiSharp.
/// </summary>
public class MediaPlayerService : IDisposable
{
    #region Fields

    private readonly object _frameLock = new();
    private Dispatcher? _dispatcher;

    private MediaFile? _mediaFile;
    private BitmapSource? _frame; // отрендеренный кадр (immutable Bgr24)
    private byte[]? _frameBuffer; // contiguous BGR-копия последнего кадра (для FaceAiSharp)
    private int _frameW, _frameH;
    private int _frameStride; // байт в строке кадра (FrameStride потока; = w*3 для типовых ширин)
    private GCHandle _frameHandle; // pinned _frameBuffer для TryGetNextFrame/TryGetFrame
    private int _frameByteCount; // логический конец кадра (FrameByteCount); дальше — sentinel-хвост
    private long _decodedFrames; // счётчик декодированных кадров (для сообщений sentinel)
    private int _sentinelHits; // сколько раз залогирован обнаруженный overrun (макс. 5)
    private int _uiUpdatePending; // 0/1: есть ли pending-колбэк обновления кадра на UI

    private volatile BitmapSource? _pendingFrame; // готовый кадр, ожидающий передачи на UI
    private TimeSpan _lastUiFrame; // время последнего UI-кадра (троттлинг)
    private static readonly TimeSpan UiFrameInterval = TimeSpan.FromMilliseconds(33); // ~30 fps

    // --- даунскейл для отображения (FaceAiSharp получает полный кадр из _frameBuffer) ---
    private const int MaxDisplayWidth = 960; // максимальная ширина кадра для экрана
    private const int SentinelPadding = 64; // хвост под SIMD-оверран sws_scale: фикс + sentinel-доказательство
    private Mat? _displaySrc; // полный кадр как Mat (переиспользуется)
    private Mat? _displayDst; // уменьшенный кадр как Mat (переиспользуется)
    private byte[]? _displayBytes; // пиксели уменьшенного кадра (переиспользуется)
    private int _dispW, _dispH; // размеры кадра для отображения

    private Task? _playTask;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _loadCts; // отмена фоновой загрузки (повторное открытие)
    private volatile bool _isPlaying;
    private TimeSpan _position; // PTS последнего декодированного кадра
    private TimeSpan _playStartPos; // _position в момент нажатия Play
    private bool _disposed;

    #endregion

    #region Properties and events

    public PlayerStatus Status { get; private set; } = PlayerStatus.Stopped;
    public long CurTime => (long)Math.Max(0, _position.TotalMilliseconds);

    public long DurationMs => _mediaFile != null
        ? (long)_mediaFile.Info.Duration.TotalMilliseconds
        : 0;

    public BitmapSource? VideoFrame => _frame;
    public VideoInfo? VideoInfo { get; private set; }
    public bool IsInitialized { get; private set; }
    public bool IsPlaying => _isPlaying;
    public bool IsMediaLoaded => _mediaFile != null;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<int>? SeekCompleted; // ms; -1 = перемотка не удалась
    public event EventHandler? PlaybackStopped;

    #endregion

    #region Init / load

    public void Initialize(Dispatcher dispatcher)
    {
        if (IsInitialized)
            return; // движок уже запущен

        _dispatcher = dispatcher;

        var path = SettingsManager.CurrentSettings.FfmpegPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return; // OpenFile покажет сообщение

        // FFMediaToolkit требует DLL shared-сборки FFmpeg 7.x в этой папке
        FFmpegLoader.FFmpegPath = path;
        FFmpegLoader.LoadFFmpeg();

        // Нативные логи ffmpeg → Debug: если краш повторится, в выводе видно точное место
        // (декодер/sws_scale). Оставить на время проверки, затем можно убрать.
        FFmpegLoader.SetupLogging();
        FFmpegLoader.LogCallback += msg => Debug.WriteLine($"[ffmpeg] {msg}");

        IsInitialized = true;
    }

    public void LoadMedia(string filePath)
    {
        if (!IsInitialized)
            return;

        StopPlayback();

        _mediaFile?.Dispose();
        _mediaFile = null;
        if (_frameHandle.IsAllocated)
            _frameHandle.Free();
        _frame = null;
        _frameBuffer = null;
        VideoInfo = null;
        _position = TimeSpan.Zero;
        RaiseUi(() =>
        {
            OnPropertyChanged(nameof(VideoFrame));
            OnPropertyChanged(nameof(CurTime));
            OnPropertyChanged(nameof(DurationMs));
            OnPropertyChanged(nameof(IsMediaLoaded));
            OnPropertyChanged(nameof(VideoInfo));
        });

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        _ = Task.Run(() => LoadMediaCore(filePath, token));
    }


    private void LoadMediaCore(string filePath, CancellationToken token)
    {
        MediaFile? mediaFile = null;
        GCHandle handle = default;
        byte[]? buffer = null;
        int w = 0, h = 0;
        int stride = 0;
        int frameByteCount = 0;
        double fps = 0;
        bool ok = false;
        TimeSpan pos = TimeSpan.Zero;

        try
        {
            var options = new MediaOptions
            {
                StreamsToLoad = MediaMode.Video,
                VideoPixelFormat = ImagePixelFormat.Bgr24,
                DecoderThreads = 1, // отключаем frame threading: при auto h264 переиспользует
                                    // AVFrame->data, и sws_scale может читать освобождённую
                                    // память → повреждение кучи (краш только на mp4)
                DemuxerOptions = new ContainerOptions { SeekToAny = true }
            };
            mediaFile = MediaFile.Open(filePath, options);
            if (token.IsCancellationRequested)
                return;

            var video = mediaFile.Video;
            if (video == null)
            {
                mediaFile.Dispose();
                SetStatus(PlayerStatus.Failed);
                return;
            }

            var size = video.Info.FrameSize;
            w = size.Width;
            h = size.Height;
            stride = video.FrameStride; // stride, который ожидает TryGetFrame/TryGetNextFrame
            frameByteCount = video.FrameByteCount;

            fps = video.Info.AvgFrameRate;

            // +SentinelPadding: хвост за кадром. sws_scale (SIMD) может дописать до 64 байт
            // за последнюю строку; раньше это была чужая управляемая память → краш GC.
            buffer = new byte[frameByteCount + SentinelPadding];
            handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            for (int i = frameByteCount; i < buffer.Length; i++)
                buffer[i] = 0xAA; // sentinel

            ok = video.TryGetFrame(TimeSpan.Zero, handle.AddrOfPinnedObject(), stride);
            if (ok)
            {
                pos = video.Position;
                VerifySentinel(buffer, frameByteCount);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Load media failed: {ex.Message}");
            if (handle.IsAllocated)
                handle.Free();
            mediaFile?.Dispose();
            SetStatus(PlayerStatus.Failed);
            return;
        }

        RaiseUi(() =>
        {
            if (token.IsCancellationRequested)
            {
                if (handle.IsAllocated)
                    handle.Free();
                mediaFile?.Dispose();
                return;
            }

            _mediaFile = mediaFile;
            _frameBuffer = buffer;
            _frameHandle = handle;
            _frameW = w;
            _frameH = h;
            _frameStride = stride;
            _frameByteCount = frameByteCount;
            _position = pos;

            VideoInfo = new VideoInfo { Width = w, Height = h, Fps = fps };

            // даунскейл для отображения: окно jauh меньше 4K, не стоит копировать 25 МБ/кадр в BitmapSource (LOH-GC = фризы)
            _dispW = Math.Min(w, MaxDisplayWidth);
            _dispH = (int)Math.Round(h * (double)_dispW / w);
            if (_dispW < w)
            {
                _displaySrc = new Mat(h, w, MatType.CV_8UC3);
                _displayDst = new Mat(_dispH, _dispW, MatType.CV_8UC3);
                _displayBytes = new byte[_dispW * _dispH * 3];
            }

            if (ok)
                UpdateFrameOnUi();

            OnPropertyChanged(nameof(VideoFrame));
            OnPropertyChanged(nameof(CurTime));
            OnPropertyChanged(nameof(DurationMs));
            OnPropertyChanged(nameof(IsMediaLoaded));
            SetStatus(ok ? PlayerStatus.Paused : PlayerStatus.Failed);
        });
    }

    #endregion

    #region Playback

    public void Play()
    {
        if (_mediaFile?.Video == null || _isPlaying || Status == PlayerStatus.Failed)
            return;

        if (Status == PlayerStatus.Stopped)
            SeekTo(0); // после конца файла — сначала в начало

        _cts = new CancellationTokenSource();
        _isPlaying = true;
        _playStartPos = _position;
        SetStatus(PlayerStatus.Playing);

        _playTask = Task.Run(() => DecodeLoop(_cts.Token));
    }

    public void Pause()
    {
        if (Status != PlayerStatus.Playing)
            return;
        StopPlayback();
        SetStatus(PlayerStatus.Paused);
    }

    public void PlayPause()
    {
        if (IsPlaying)
            Pause();
        else
            Play();
    }

    /// <summary>
    /// Точная перемотка на ms. Работает из любого состояния; после seek на паузе
    /// поднимает SeekCompleted(ms) — ViewModel по нему запускает FaceAiSharp.
    /// </summary>
    public void SeekTo(int ms)
    {
        if (_mediaFile?.Video == null)
            return;

        int maxMs = (int)_mediaFile.Info.Duration.TotalMilliseconds;
        int targetMs = Math.Clamp(ms, 0, Math.Max(0, maxMs));
        var time = TimeSpan.FromMilliseconds(targetMs);

        StopPlayback(); // дожидаемся остановки decode-потока

        var video = _mediaFile.Video!;
        bool ok;
        lock (_frameLock)
            ok = video.TryGetFrame(time, _frameHandle.AddrOfPinnedObject(), _frameStride);
        if (ok)
            VerifySentinel(_frameBuffer!, _frameByteCount);

        if (!ok)
        {
            RaiseUi(() => SeekCompleted?.Invoke(this, -1));
            return;
        }

        _position = video.Position;
        UpdateFrameOnUi();


        RaiseUi(() =>
        {
            OnPropertyChanged(nameof(CurTime));
            SeekCompleted?.Invoke(this, targetMs);
        });
        SetStatus(PlayerStatus.Paused);
    }

    #endregion

    #region Frame for FaceAiSharp

    /// <summary>
    /// Текущий кадр как OpenCvSharp Mat (BGR, CV_8UC3).
    /// Работает и на паузе — возвращает копию последнего отрендеренного кадра.
    /// </summary>
    public Mat? GetCurrentFrame()
    {
        lock (_frameLock)
        {
            if (_frameBuffer == null || _frameW <= 0 || _frameH <= 0)
                return null;

            // явная копия: буфер сервиса переиспользуется следующим декодом
            var mat = new Mat(_frameH, _frameW, MatType.CV_8UC3);
            FillMatFromStrided(_frameBuffer, _frameStride, _frameW, _frameH, mat);
            return mat;
        }
    }

    /// <summary>
    /// Заполняет Mat (BGR, CV_8UC3) из буфера с произвольным stride.
    /// При stride == w*3 — прямой Marshal.Copy; иначе — построчно.
    /// </summary>
    private static void FillMatFromStrided(byte[] src, int srcStride, int w, int h, Mat dst)
    {
        int rowBytes = w * 3;
        if (srcStride == rowBytes)
        {
            Marshal.Copy(src, 0, dst.Data, rowBytes * h);
            return;
        }
        for (int y = 0; y < h; y++)
            Marshal.Copy(src, y * srcStride, dst.Data + y * rowBytes, rowBytes);
    }

    /// <summary>
    /// Копирует буфер с произвольным stride в row-aligned byte[] (BGR).
    /// При stride == w*3 возвращает исходный массив без копирования.
    /// </summary>
    private static byte[] CopyStridedToBytes(byte[] src, int srcStride, int w, int h)
    {
        int rowBytes = w * 3;
        if (srcStride == rowBytes)
            return src;
        var dst = new byte[rowBytes * h];
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(src, y * srcStride, dst, y * rowBytes, rowBytes);
        return dst;
    }

    /// <summary>
    /// Диагностика overrun'а: проверяет, не дописал ли sws_scale в sentinel-хвост за кадром.
    /// Если да — прямое доказательство, что нативка писала за пределы буфера (раньше — чужая куча).
    /// </summary>
    private void VerifySentinel(byte[] buffer, int logicalEnd)
    {
        for (int i = logicalEnd; i < buffer.Length; i++)
        {
            if (buffer[i] != 0xAA)
            {
                int last = i;
                for (int j = i + 1; j < buffer.Length; j++)
                {
                    if (buffer[j] != 0xAA)
                        last = j;
                    else
                        break;
                }
                if (_sentinelHits < 5)
                {
                    _sentinelHits++;
                    Debug.WriteLine($"[SENTINEL] OVERRUN #{_sentinelHits}: sws_scale wrote {last - i + 1} byte(s) past the frame " +
                        $"buffer (offsets {i}..{last}; logical end {logicalEnd}, buffer {buffer.Length}) at frame #{_decodedFrames}. " +
                        $"Heap corruption confirmed; padding now absorbs it.");
                }
                return;
            }
        }
    }

    /// <summary>
    /// Строит кадр для отображения на ВЫЗЫВАЮЩЕМ потоке (decode или UI) и
    /// передаёт готовый битмап на UI-поток. Тяжёлая работа (копия 25 МБ,
    /// resize, BitmapSource.Create) во время воспроизведения выполняется
    /// на decode-потоке; сам UI-колбэк дешёвый: только подмена ссылки.
    /// </summary>
    private void UpdateFrameOnUi()
    {
        var buffer = _frameBuffer;
        if (buffer == null)
            return;

        int w = _frameW, h = _frameH;
        BitmapSource bmp;
        lock (_frameLock)
        {
            if (_displaySrc != null && _displayDst != null && _displayBytes != null)
            {
                // даунскейл: полный кадр -> уменьшенный. Mats переиспользуются,
                // аллоцируется только (маленький) внутренний буфер BitmapSource.
                FillMatFromStrided(buffer, _frameStride, w, h, _displaySrc);
                Cv2.Resize(_displaySrc, _displayDst, new OpenCvSharp.Size(_dispW, _dispH), 0, 0,
                    InterpolationFlags.Area);
                Marshal.Copy(_displayDst.Data, _displayBytes, 0, _displayBytes.Length);
                bmp = BitmapSource.Create(_dispW, _dispH, 96, 96, PixelFormats.Bgr24, null, _displayBytes, _dispW * 3);
            }
            else
            {
                // видео не шире MaxDisplayWidth — без даунскейла
                var pixels = CopyStridedToBytes(buffer, _frameStride, w, h); // == buffer при w*3
                bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, pixels, w * 3);
            }
        }

        bmp.Freeze(); // immutable + thread-agnostic: безопасно передать на UI-поток
        _pendingFrame = bmp;

        if (_dispatcher == null || _dispatcher.CheckAccess())
        {
            ApplyFrameOnUi(); // уже на UI-потоке (seek/load) — сразу
        }
        else
        {
            // decode-поток: latest-value-wins, максимум один pending-колбэк на UI.
            if (Interlocked.Exchange(ref _uiUpdatePending, 1) == 0)
                _dispatcher.BeginInvoke(new Action(ApplyFrameOnUiDispatcher));
        }
    }

    private void ApplyFrameOnUiDispatcher()
    {
        Interlocked.Exchange(ref _uiUpdatePending, 0);
        ApplyFrameOnUi();
    }

    private void ApplyFrameOnUi()
    {
        var bmp = _pendingFrame;
        if (bmp == null)
            return;

        _frame = bmp;
        OnPropertyChanged(nameof(VideoFrame));
        OnPropertyChanged(nameof(CurTime));
    }

    #endregion

    #region Decode loop

    private void DecodeLoop(CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        _lastUiFrame = TimeSpan.Zero; // сброс троттлинга: sw новый, иначе первые кадры не дойдут до UI
        try
        {
            var video = _mediaFile!.Video!;
            var ptr = _frameHandle.AddrOfPinnedObject();
            int stride = _frameStride;

            while (!token.IsCancellationRequested)
            {
                bool ok;
                lock (_frameLock)
                {
                    ok = video.TryGetNextFrame(ptr, stride);
                }
                if (ok)
                {
                    _decodedFrames++;
                    VerifySentinel(_frameBuffer!, _frameByteCount);
                }

                if (!ok)
                {
                    EndOfStream();
                    return;
                }

                _position = video.Position;

                if (sw.Elapsed - _lastUiFrame >= UiFrameInterval)
                {
                    _lastUiFrame = sw.Elapsed;
                    UpdateFrameOnUi();
                }

                var target = _position - _playStartPos;
                var sleep = target - sw.Elapsed;
                if (sleep > TimeSpan.Zero)
                    Thread.Sleep(sleep);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Decode error: {ex}"); // полный текст + стек
            SetStatus(PlayerStatus.Failed);
            FirePlaybackStopped();
        }
    }

    private void StopPlayback()
    {
        _isPlaying = false;
        _cts?.Cancel();
        try
        {
            _playTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
        }

        _playTask = null;
        _cts?.Dispose();
        _cts = null;
    }

    private void EndOfStream()
    {
        _isPlaying = false;
        SetStatus(PlayerStatus.Stopped);
        FirePlaybackStopped();
    }

    private void FirePlaybackStopped()
    {
        RaiseUi(() => PlaybackStopped?.Invoke(this, EventArgs.Empty));
    }

    #endregion

    #region Events

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SetStatus(PlayerStatus value)
    {
        RaiseUi(() =>
        {
            if (Status == value)
                return;
            Status = value;
            OnPropertyChanged(nameof(Status));
        });
    }

    /// <summary>
    /// Маршалим действие на UI-поток (события не должны подниматься из decode-потока).
    /// </summary>
    private void RaiseUi(Action action)
    {
        if (_dispatcher == null || _dispatcher.CheckAccess())
            action();
        else
            _dispatcher.BeginInvoke(action);
    }

    #endregion

    #region Dispose

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            StopPlayback();
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = null;
            _mediaFile?.Dispose();
            _mediaFile = null;
            if (_frameHandle.IsAllocated)
                _frameHandle.Free();
            _frame = null;
            _frameBuffer = null;
            _displaySrc?.Dispose();
            _displaySrc = null;
            _displayDst?.Dispose();
            _displayDst = null;
            _displayBytes = null;
        }

        _disposed = true;
    }

    ~MediaPlayerService() => Dispose(false);

    #endregion
}