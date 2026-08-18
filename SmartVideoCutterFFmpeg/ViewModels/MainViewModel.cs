using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using OpenCvSharp;
using System.Windows.Media.Imaging;


namespace SmartVideoCutterFFmpeg.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    #region Properties

    [ObservableProperty] private string _filePath = string.Empty;
    [ObservableProperty] private List<Keyframe> _keyframeList = new();
    [ObservableProperty] private bool _isKeyframePanelExpanded;
    [ObservableProperty] private string _statusMessage = "Откройте видео файл";
    [ObservableProperty] private List<FaceBox> _faceBoxes = new();
    [ObservableProperty] private double _videoAspect; // width/height кадра, для letterbox в view
    [ObservableProperty] private int _selectedFaceIndex = -1; // индекс выбранной рамки (-1 = нет)
    [ObservableProperty] private BitmapSource? _videoFrame; // кадр для Image в контроле плеера
    [ObservableProperty] private long _positionMs; // текущее время (слайдер, метка)
    [ObservableProperty] private long _durationMs; // длительность видео (слайдер)
    [ObservableProperty] private bool _isMediaLoaded; // файл загружен (слайдер активен)

    private readonly MediaPlayerService _mediaPlayerService;
    private YoloFaceDetector? _faceDetector;
    private int _faceAnalysisSeq;
    private bool _disposed;
    private long _lastCurTime;
    private SelectedFace? _selectedFace; // данные выбора (кроп) для ArcFace
    private readonly Dispatcher _uiDispatcher;


    public VideoInfo? VideoInfo => _mediaPlayerService.VideoInfo;

    #endregion

    public MainViewModel()
    {
        _uiDispatcher = Dispatcher.CurrentDispatcher;

        _mediaPlayerService = new MediaPlayerService();

        // Плеер инициализируется до создания контрола FlyleafME - рендер-поверхность создаётся один раз при загрузке
        // Если FFmpeg не настроен — no-op, OpenFile покажет сообщение.
        TryInitializePlayer();

        try
        {
            _faceDetector = new YoloFaceDetector();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Face detector init failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenFile()
    {
        var dlg = new OpenFileDialog { Filter = "Video files|*.avi;*.mov;*.mkv;*.mp4" };
        if (dlg.ShowDialog() != true)
            return;

        if (!_mediaPlayerService.IsInitialized)
        {
            StatusMessage = "Укажите пути в настройках (Settings) и перезапустите приложение";
            return;
        }

        FilePath = dlg.FileName;

        FaceBoxes = new List<FaceBox>();
        ClearFaceSelection();
        VideoAspect = 0;

        KeyframeList = new List<Keyframe>();
        IsKeyframePanelExpanded = false;

        _mediaPlayerService.LoadMedia(FilePath);
    }


    [RelayCommand]
    private void OpenSettings()
    {
        var settingsWindow = new SettingsWindow();
        settingsWindow.ShowDialog();
    }

    partial void OnFilePathChanged(string oldValue, string newValue)
    {
        /*
        Когда генератор RelayCommand находит зависимости в CanExecute,
        он генерирует частичный метод OnFilePathChanged с реализацией AnalyzeFileCommand.NotifyCanExecuteChanged().
        Нереализованные partial-методы выбрасываются из сборки — а его нет.
        То есть генератор не сгенерировал подписку на изменения FilePath.

        Это известное ограничение CommunityToolkit.Mvvm:
        генератор RelayCommand отслеживает зависимости только по объявленным в исходнике свойствам.
        А FilePath — свойство, сгенерированное самим же [ObservableProperty] (в исходнике есть только поле _filePath).
        Генераторы в одном проходе не видят вывод друг друга →
            зависимость не отслеживается →
                CanExecute вычисляется один раз при создании (false) и никогда не обновляется.
         */

        AnalyzeFileCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ExportFile()
    {
    }


    #region Player

    private void TryInitializePlayer()
    {
        var settings = SettingsManager.CurrentSettings;
        if (string.IsNullOrWhiteSpace(settings.FfmpegPath) ||
            !File.Exists(Path.Combine(settings.FfmpegPath, "ffmpeg.exe")))
            return; // OpenFile покажет сообщение

        try
        {
            _mediaPlayerService.Initialize(Dispatcher.CurrentDispatcher);

            _mediaPlayerService.PropertyChanged += OnPlayerPropertyChanged;
            _mediaPlayerService.PlaybackStopped += OnPlaybackStopped;
            _mediaPlayerService.SeekCompleted += OnSeekCompleted;
        }
        catch (Exception ex)
        {
            StatusMessage = "Ошибка инициализации плеера: " + ex.Message;
            System.Diagnostics.Debug.WriteLine($"Player init failed: {ex.Message}");
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
            StatusMessage = _mediaPlayerService.Status.ToString();

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

    private void OnSeekCompleted(object? sender, int ms)
    {
        // Событие приходит из фонового потока seek — маршализуем в UI
        _uiDispatcher.BeginInvoke(() =>
        {
            if (ms < 0)
                return; // перемотка не удалась

            if (_mediaPlayerService.Status == PlayerStatus.Paused)
                _ = AnalyzeFaces(); // перемотка закончилась на паузе — рисуем новые рамки
        });
    }

    private void OnPlaybackStopped(object? sender, EventArgs e)
    {
        _uiDispatcher.BeginInvoke(() =>
        {
            StatusMessage = _mediaPlayerService.Status == PlayerStatus.Failed
                ? "Ошибка воспроизведения"
                : "Воспроизведение завершено";
        });
    }

    [RelayCommand]
    private void SeekToTimestamp(Keyframe? keyframe)
    {
        if (keyframe != null)
        {
            _mediaPlayerService.SeekTo((int)keyframe.Timestamp);
        }
    }

    [RelayCommand]
    private void PlayPause() => _mediaPlayerService.PlayPause();

    /// <summary>Перемотка со слайдера контрола плеера.</summary>
    public void SeekToPosition(int ms) => _mediaPlayerService.SeekTo(ms);

    public bool IsPlaying => _mediaPlayerService.IsPlaying;

    public void Play() => _mediaPlayerService.Play();

    public void Pause() => _mediaPlayerService.Pause();

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
            StatusMessage = "Укажите файл модели YOLO в настройках (Settings) и перезапустите приложение";
            return;
        }

        double w = frame.Width, h = frame.Height;

        List<OpenCvSharp.Rect> rects;
        try
        {
            rects = await Task.Run(() => detector.Detect(frame));
        }
        catch (Exception ex)
        {
            frame.Dispose();
            System.Diagnostics.Debug.WriteLine($"Face detection failed: {ex.Message}");
            return;
        }

        frame.Dispose();

        if (seq != _faceAnalysisSeq || _mediaPlayerService.Status != PlayerStatus.Paused)
            return;

        VideoAspect = w / h;
        FaceBoxes = rects.Select(r => new FaceBox
        {
            X = r.X / w,
            Y = r.Y / h,
            W = r.Width / w,
            H = r.Height / h
        }).ToList();
    }


    /// Margin вокруг рамки лица при кропе для ArcFace (доля от размера рамки, на каждую сторону).
    private const double FaceCropMargin = 0.35;

    /// Выбирает рамку лица по индексу: подсвечивает её и готовит кроп для ArcFace.
    public void SelectFace(int index)
    {
        if (index < 0 || index >= FaceBoxes.Count)
            return;

        var frame = _mediaPlayerService.GetCurrentFrame();
        if (frame == null)
            return;

        try
        {
            var box = FaceBoxes[index];

            // Рамка в пикселях исходного кадра
            var boxPx = new OpenCvSharp.Rect(
                (int)(box.X * frame.Width),
                (int)(box.Y * frame.Height),
                (int)(box.W * frame.Width),
                (int)(box.H * frame.Height));

            // Кроп с margin для ArcFace
            var crop = CropFaceWithMargin(frame, boxPx);

            // Заменяем предыдущий выбор (освобождаем старый кроп)
            _selectedFace?.Dispose();
            _selectedFace = new SelectedFace(crop, boxPx, _mediaPlayerService.CurTime);
            SelectedFaceIndex = index;
            AnalyzeFileCommand.NotifyCanExecuteChanged(); // генератор не видит SelectedFaceIndex как зависимость
        }
        finally
        {
            frame.Dispose();
        }
    }

    /// Сбрасывает выбор (play, перемотка, новый файл).
    private void ClearFaceSelection()
    {
        if (_selectedFace != null)
        {
            _selectedFace.Dispose();
            _selectedFace = null;
        }

        if (SelectedFaceIndex != -1)
        {
            SelectedFaceIndex = -1;
            AnalyzeFileCommand.NotifyCanExecuteChanged(); // генератор не видит SelectedFaceIndex как зависимость
        }
    }

    /// Вырезает лицо с margin, ограничивая рамками кадра. Возвращает копию (Clone).
    private static Mat CropFaceWithMargin(Mat frame, OpenCvSharp.Rect box)
    {
        int marginX = (int)(box.Width * FaceCropMargin);
        int marginY = (int)(box.Height * FaceCropMargin);

        int x = Math.Max(0, box.X - marginX);
        int y = Math.Max(0, box.Y - marginY);
        int right = Math.Min(frame.Width, box.X + box.Width + marginX);
        int bottom = Math.Min(frame.Height, box.Y + box.Height + marginY);

        int w = right - x;
        int h = bottom - y;
        if (w <= 0 || h <= 0)
            return new Mat();

        return new Mat(frame, new OpenCvSharp.Rect(x, y, w, h)).Clone();
    }

    #endregion

    #region Face recognition

    [RelayCommand(CanExecute = nameof(CanAnalyzeFile))]
    private async void AnalyzeFile()
    {
        if (!_mediaPlayerService.IsInitialized)
        {
            StatusMessage = "Укажите пути в настройках (Settings) и перезапустите приложение";
            return;
        }

        var progressDialog = new ProgressDialog("Анализ видео", "Получение ключевых кадров...");
        progressDialog.Owner = Application.Current.MainWindow;
        progressDialog.Show();

        KeyframeList = await Task.Run(() => FFmpegService.GetVideoKeyframes(FilePath));

        IsKeyframePanelExpanded = true;

        progressDialog.Close();
    }


    private bool CanAnalyzeFile() => SelectedFaceIndex >= 0;

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

            _mediaPlayerService.Dispose();
            _faceDetector?.Dispose();
        }

        _disposed = true;
    }

    #endregion
}