using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartVideoCutter.Models.ComputerVision;
using SmartVideoCutter.Models.Video;
using SmartVideoCutter.Services;
using SmartVideoCutter.Services.ComputerVision;
using SmartVideoCutter.Services.Video;

namespace SmartVideoCutter.ViewModels;

/// <summary>
/// Child-ViewModel видео-плеера: кадр, позиция, рамки лиц.
/// DataContext для <see cref="Views.VideoPlayerControl"/> (устанавливается в MainWindow.xaml).
/// Сервисы (плеер, детектор, распознаватель) принадлежат <see cref="MainViewModel"/>.
/// </summary>
public partial class PlayerViewModel : ObservableObject, IDisposable
{
    #region Properties

    [ObservableProperty] private BitmapSource? _videoFrame; // кадр для Image в контроле плеера
    [ObservableProperty] private long _positionMs; // текущее время (слайдер, метка)
    [ObservableProperty] private long _durationMs; // длительность видео (слайдер)
    [ObservableProperty] private bool _isMediaLoaded; // файл загружен (слайдер активен)
    [ObservableProperty] private double _videoAspect; // width/height кадра, для letterbox в view
    [ObservableProperty] private List<FaceBox> _faceBoxes = new();
    [ObservableProperty] private int _selectedFaceIndex = -1; // индекс выбранной рамки (-1 = нет)
    [ObservableProperty] private bool _isDragging; // пользователь перетаскивает бегунок слайдера

    private readonly MediaPlayerService _mediaPlayerService;
    private readonly FaceDetector? _faceDetector;
    private readonly FaceRecognizer? _faceRecognizer;

    private List<DetectedFace> _detectedFaces = new();
    private int _faceAnalysisSeq;
    private long _lastCurTime;
    private bool _wasPlaying; // видео играло в момент начала перетаскивания слайдера
    private SelectedFace? _selectedFace; // данные выбора (кроп) для ArcFace
    private bool _disposed;

    /// <summary>Выбранное лицо (embedding) для анализа.</summary>
    public SelectedFace? SelectedFace => _selectedFace;

    public VideoInfo? VideoInfo => _mediaPlayerService.VideoInfo;
    public bool IsPlaying => _mediaPlayerService.IsPlaying;
    public bool IsInitialized => _mediaPlayerService.IsInitialized;

    /// <summary>Ошибка инициализации плеера (null — FFmpeg не настроен или инициализация успешна).</summary>
    public string? InitError { get; private set; }

    /// <summary>Изменился выбор лица (родительский VM обновляет CanExecute AnalyzeFile).</summary>
    public event Action? FaceSelectionChanged;

    /// <summary>Статус-сообщение для статус-бара главного окна.</summary>
    public event Action<string>? StatusMessageChanged;

    #endregion

    public PlayerViewModel(MediaPlayerService media, FaceDetector? detector, FaceRecognizer? recognizer)
    {
        _mediaPlayerService = media;
        _faceDetector = detector;
        _faceRecognizer = recognizer;
    }

    /// <summary>
    /// Инициализирует плеер, если FFmpeg настроен.
    /// Возвращает false, если FFmpeg не настроен (OpenFile покажет сообщение).
    /// </summary>
    public bool Initialize()
    {
        var settings = SettingsManager.CurrentSettings;
        if (string.IsNullOrWhiteSpace(settings.FfmpegPath) ||
            !File.Exists(Path.Combine(settings.FfmpegPath, "ffmpeg.exe")))
            return false; // OpenFile покажет сообщение

        try
        {
            _mediaPlayerService.Initialize();

            _mediaPlayerService.PropertyChanged += OnPlayerPropertyChanged;
            _mediaPlayerService.PlaybackStopped += OnPlaybackStopped;
            _mediaPlayerService.SeekCompleted += OnSeekCompleted;
            return true;
        }
        catch (Exception ex)
        {
            InitError = ex.Message;
            Debug.WriteLine($"Player init failed: {ex.Message}");
            return false;
        }
    }

    #region Player

    public void LoadMedia(string filePath) => _mediaPlayerService.LoadMedia(filePath);

    /// <summary>Сбрасывает состояние плеера при открытии нового файла (рамки, aspect, выбор).</summary>
    public void Reset()
    {
        FaceBoxes = new List<FaceBox>();
        VideoAspect = 0;
        ClearFaceSelection();
    }

    [RelayCommand]
    private void PlayPause() => _mediaPlayerService.PlayPause();

    [RelayCommand]
    private void Play() => _mediaPlayerService.Play();

    [RelayCommand]
    private void Pause() => _mediaPlayerService.Pause();

    /// <summary>Перемотка со слайдера контрола плеера.</summary>
    [RelayCommand]
    private void SeekToPosition(int ms) => _mediaPlayerService.SeekTo(ms);

    /// <summary>
    /// Начало перетаскивания бегунка слайдера: ставим паузу и забираем
    /// управление PositionMs (TwoWay-биндинг слайдера пишет в него напрямую;
    /// CurTime-обновления игнорируются, см. OnPlayerPropertyChanged).
    /// </summary>
    [RelayCommand]
    private void StartDragging()
    {
        IsDragging = true;
        _wasPlaying = IsPlaying;
        if (_wasPlaying)
            _mediaPlayerService.Pause();
    }

    /// <summary>
    /// Конец перетаскивания: перемотка на позицию бегунка,
    /// возобновление воспроизведения, если оно было.
    /// </summary>
    [RelayCommand]
    private void EndDragging()
    {
        if (!IsDragging)
            return;
        IsDragging = false;

        _mediaPlayerService.SeekTo((int)PositionMs);
        if (_wasPlaying)
            _mediaPlayerService.Play();
        _wasPlaying = false;
    }

    /// <summary>Перемотка на ключевой кадр (двойной клик в DataGrid).</summary>
    [RelayCommand]
    private void SeekToTimestamp(Keyframe? keyframe)
    {
        if (keyframe != null)
        {
            _mediaPlayerService.SeekTo((int)keyframe.Timestamp);
        }
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaPlayerService.VideoFrame))
        {
            VideoFrame = _mediaPlayerService.VideoFrame;
        }
        else if (e.PropertyName == nameof(MediaPlayerService.Status))
        {
            StatusMessageChanged?.Invoke(_mediaPlayerService.Status.ToString());

            if (_mediaPlayerService.Status == PlayerStatus.Paused)
            {
                ClearFaceSelection(); // новый кадр — сбрасываем выбор
                _lastCurTime = _mediaPlayerService.CurTime;
                _ = AnalyzeFaces();
            }
            else
            {
                FaceBoxes = new List<FaceBox>(); // Play/Stop: убираем рамки с экрана
                ClearFaceSelection();
            }
        }
        else if (e.PropertyName == nameof(MediaPlayerService.CurTime))
        {
            long t = _mediaPlayerService.CurTime;
            bool changed = t != _lastCurTime;
            _lastCurTime = t;

            // Пока пользователь перетаскивает бегунок, не перезаписываем
            // PositionMs из CurTime (в т.ч. queued-обновления, закэшированные
            // до паузы): значение слайдера — источник правды.
            if (!IsDragging)
                PositionMs = t; // слайдер и метка времени

            // Начало перемотки на паузе: убираем устаревшие рамки
            // (новые появятся после SeekCompleted)
            if (changed && _mediaPlayerService.Status == PlayerStatus.Paused)
            {
                FaceBoxes = new List<FaceBox>();
                ClearFaceSelection();
            }
        }
        else if (e.PropertyName == nameof(MediaPlayerService.DurationMs))
        {
            DurationMs = _mediaPlayerService.DurationMs;
        }
        else if (e.PropertyName == nameof(MediaPlayerService.IsMediaLoaded))
        {
            IsMediaLoaded = _mediaPlayerService.IsMediaLoaded;
        }
    }

    // События MediaPlayerService (SeekCompleted/PlaybackStopped) уже поднимаются
    // на UI-потоке (RaiseUi в сервисе) — дополнительная маршализация не нужна.

    private void OnSeekCompleted(object? sender, int ms)
    {
        if (ms < 0)
            return; // перемотка не удалась

        if (_mediaPlayerService.Status == PlayerStatus.Paused)
            _ = AnalyzeFaces(); // перемотка закончилась на паузе — рисуем новые рамки
    }

    private void OnPlaybackStopped(object? sender, EventArgs e)
    {
        StatusMessageChanged?.Invoke(_mediaPlayerService.Status == PlayerStatus.Failed
            ? "Ошибка воспроизведения"
            : "Воспроизведение завершено");
    }

    #endregion

    #region Face detection

    private async Task AnalyzeFaces()
    {
        var detector = _faceDetector;
        var frame = _mediaPlayerService.GetCurrentFrame();
        int seq = ++_faceAnalysisSeq;

        if (frame == null)
            return;

        if (detector == null)
        {
            StatusMessageChanged?.Invoke("Не удалось инициализировать FaceAiSharp (проверьте папку onnx в bin)");
            return;
        }

        double w = frame.Width, h = frame.Height;

        List<DetectedFace> faces;
        try
        {
            faces = await Task.Run(() => detector.Detect(frame));
        }
        catch (Exception ex)
        {
            frame.Dispose();
            Debug.WriteLine($"Face detection failed: {ex.Message}");
            return;
        }

        frame.Dispose();

        if (seq != _faceAnalysisSeq || _mediaPlayerService.Status != PlayerStatus.Paused)
            return;

        _detectedFaces = faces;
        VideoAspect = w / h;
        FaceBoxes = faces.Select((f, i) => new FaceBox
        {
            Index = i,
            X = f.BoxPx.X / w,
            Y = f.BoxPx.Y / h,
            W = f.BoxPx.Width / w,
            H = f.BoxPx.Height / h
        }).ToList();
    }

    /// <summary>Выбирает рамку лица по индексу: подсвечивает её и считает embedding ArcFace.</summary>
    [RelayCommand]
    private void SelectFace(int index)
    {
        if (index < 0 || index >= FaceBoxes.Count || index >= _detectedFaces.Count)
            return;

        var recognizer = _faceRecognizer;
        var face = _detectedFaces[index];
        if (recognizer == null || face.Landmarks == null)
            return;

        var frame = _mediaPlayerService.GetCurrentFrame();
        if (frame == null)
            return;

        try
        {
            float[] embedding;
            try
            {
                embedding = recognizer.GenerateReferenceEmbedding(frame, face.BoxPx, face.Landmarks);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Embedding failed: {ex.Message}");
                return;
            }

            _selectedFace = new SelectedFace(embedding, face.BoxPx, _mediaPlayerService.CurTime);
            SelectedFaceIndex = index;
            FaceSelectionChanged?.Invoke(); // родительский VM обновляет CanExecute AnalyzeFile
        }
        finally
        {
            frame.Dispose();
        }
    }

    /// <summary>Сбрасывает выбор (play, перемотка, новый файл).</summary>
    private void ClearFaceSelection()
    {
        _selectedFace = null;

        if (SelectedFaceIndex != -1)
        {
            SelectedFaceIndex = -1;
            FaceSelectionChanged?.Invoke(); // родительский VM обновляет CanExecute AnalyzeFile
        }
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
        if (_disposed) return;

        if (disposing)
        {
            _mediaPlayerService.PropertyChanged -= OnPlayerPropertyChanged;
            _mediaPlayerService.PlaybackStopped -= OnPlaybackStopped;
            _mediaPlayerService.SeekCompleted -= OnSeekCompleted;
        }

        _disposed = true;
    }

    #endregion
}
