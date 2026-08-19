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
/// (PropertyChanged, SeekCompleted, PlaybackStopped) и GetCurrentFrame() для YOLO.
/// </summary>
public class MediaPlayerService : IDisposable
{
    #region Fields

    private readonly object _frameLock = new();
    private Dispatcher? _dispatcher;

    private MediaFile? _mediaFile;
    private BitmapSource? _frame; // отрендеренный кадр (immutable Bgr24)
    private byte[]? _frameBuffer; // contiguous BGR-копия последнего кадра (для YOLO)
    private int _frameW, _frameH;
    private GCHandle _frameHandle; // pinned _frameBuffer для TryGetNextFrame/TryGetFrame
    private int _uiUpdatePending; // 0/1: есть ли pending-колбэк обновления кадра на UI

    private volatile BitmapSource? _pendingFrame; // готовый кадр, ожидающий передачи на UI
    private TimeSpan _lastUiFrame; // время последнего UI-кадра (троттлинг)
    private static readonly TimeSpan UiFrameInterval = TimeSpan.FromMilliseconds(33); // ~30 fps

    // --- даунскейл для отображения (YOLO получает полный кадр из _frameBuffer) ---
    private const int MaxDisplayWidth = 960; // максимальная ширина кадра для экрана
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
        _position = TimeSpan.Zero;
        RaiseUi(() =>
        {
            OnPropertyChanged(nameof(VideoFrame));
            OnPropertyChanged(nameof(CurTime));
            OnPropertyChanged(nameof(DurationMs));
            OnPropertyChanged(nameof(IsMediaLoaded));
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
        bool ok = false;
        TimeSpan pos = TimeSpan.Zero;

        try
        {
            var options = new MediaOptions
            {
                StreamsToLoad = MediaMode.Video,
                VideoPixelFormat = ImagePixelFormat.Bgr24,
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
            buffer = new byte[w * h * 3];
            handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);

            ok = video.TryGetFrame(TimeSpan.Zero, handle.AddrOfPinnedObject(), w * 3);
            if (ok)
                pos = video.Position;
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
            _position = pos;

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
    /// поднимает SeekCompleted(ms) — ViewModel по нему запускает YOLO.
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
            ok = video.TryGetFrame(time, _frameHandle.AddrOfPinnedObject(), _frameW * 3);

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

    #region Frame for YOLO

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
            Marshal.Copy(_frameBuffer, 0, mat.Data, _frameW * _frameH * 3);
            return mat;
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
                Marshal.Copy(buffer, 0, _displaySrc.Data, w * h * 3);
                Cv2.Resize(_displaySrc, _displayDst, new OpenCvSharp.Size(_dispW, _dispH), 0, 0,
                    InterpolationFlags.Area);
                Marshal.Copy(_displayDst.Data, _displayBytes, 0, _displayBytes.Length);
                bmp = BitmapSource.Create(_dispW, _dispH, 96, 96, PixelFormats.Bgr24, null, _displayBytes, _dispW * 3);
            }
            else
            {
                // видео не шире MaxDisplayWidth — без даунскейла
                bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, buffer, w * 3);
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
            int stride = _frameW * 3;

            while (!token.IsCancellationRequested)
            {
                bool ok;
                lock (_frameLock)
                {
                    ok = video.TryGetNextFrame(ptr, stride);
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