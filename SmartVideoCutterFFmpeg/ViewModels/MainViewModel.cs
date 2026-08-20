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
    private FaceDetector? _faceDetector;
    private FaceRecognizer? _faceRecognizer;
    private List<DetectedFace> _detectedFaces = new();
    private int _faceAnalysisSeq;
    private bool _disposed;
    private long _lastCurTime;
    private SelectedFace? _selectedFace; // данные выбора (кроп) для ArcFace
    private bool _isAnalyzed; // анализ выполнен — кнопка Export может активироваться
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
            _faceDetector = new FaceDetector();
            _faceRecognizer = new FaceRecognizer(_faceDetector);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Face services init failed: {ex.Message}");
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

        foreach (var kf in KeyframeList)
            kf.PropertyChanged -= OnKeyframePropertyChanged;

        _isAnalyzed = false;
        ExportFileCommand.NotifyCanExecuteChanged();

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

    private void OnKeyframePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Keyframe.IsSelected))
            ExportFileCommand.NotifyCanExecuteChanged();
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
            StatusMessage = "Не удалось инициализировать FaceAiSharp (проверьте папку onnx в bin)";
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
            System.Diagnostics.Debug.WriteLine($"Face detection failed: {ex.Message}");
            return;
        }

        frame.Dispose();

        if (seq != _faceAnalysisSeq || _mediaPlayerService.Status != PlayerStatus.Paused)
            return;

        _detectedFaces = faces;
        VideoAspect = w / h;
        FaceBoxes = faces.Select(f => new FaceBox
        {
            X = f.BoxPx.X / w,
            Y = f.BoxPx.Y / h,
            W = f.BoxPx.Width / w,
            H = f.BoxPx.Height / h
        }).ToList();
    }

    /// Выбирает рамку лица по индексу: подсвечивает её и готовит кроп для ArcFace.
    /// Выбирает рамку лица по индексу: подсвечивает её и считает embedding ArcFace.
    public void SelectFace(int index)
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
                System.Diagnostics.Debug.WriteLine($"Embedding failed: {ex.Message}");
                return;
            }

            _selectedFace = new SelectedFace(embedding, face.BoxPx, _mediaPlayerService.CurTime);
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
        _selectedFace = null;

        if (SelectedFaceIndex != -1)
        {
            SelectedFaceIndex = -1;
            AnalyzeFileCommand.NotifyCanExecuteChanged(); // генератор не видит SelectedFaceIndex как зависимость
        }
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

        var recognizer = _faceRecognizer;
        var reference = _selectedFace?.Embedding;
        var videoInfo = _mediaPlayerService.VideoInfo;
        if (recognizer == null || reference == null || videoInfo == null)
            return;

        int w = videoInfo.Width, h = videoInfo.Height;

        // 1. Список ключевых кадров (ffprobe, быстро) — границы отрезков
        var keyframes = await Task.Run(() => FFmpegService.GetVideoKeyframes(FilePath));
        if (keyframes.Count == 0)
            return;

        double durationMs = _mediaPlayerService.DurationMs;

        // 2. Окно с детерминированным прогрессом: maximum = число ключевых кадров
        var progressDialog = new ProgressDialog("Анализ видео", "Поиск лица по кадрам...",
            indeterminate: false, maximum: keyframes.Count);
        progressDialog.Owner = Application.Current.MainWindow;
        progressDialog.Show();
        var ct = progressDialog.Cts.Token;

        // 3. Анализ кадров каждого отрезка [keyframe[i], keyframe[i+1]) — алгоритм из настроек.
        //    Логика в FaceRecognizer: нашли человека — отрезок принят, остальные кадры
        //    пропускаем (ранний выход). Отмена: выходим до следующей итерации.
        Exception? error = null;
        try
        {
            await Task.Run(() => recognizer.Analyze(
                FilePath, keyframes, durationMs, w, h, videoInfo.Fps, reference,
                SettingsManager.CurrentSettings.AnalysisAlgorithm,
                i =>
                {
                    progressDialog.UpdateProgress(i);
                    progressDialog.UpdateMessage($"Поиск лица по кадрам... ({i}/{keyframes.Count})");
                },
                ct));
        }
        catch (Exception ex)
        {
            // без catch исключение из ReadFrames в async void методе = краш всего приложения
            error = ex;
            System.Diagnostics.Debug.WriteLine($"Analyze failed: {ex}");
        }

        // ВАЖНО: состояние отмены фиксировать ДО Close(): WPF синхронно вызывает OnClosed,
        // а ProgressDialog.OnClosed → Cts.Cancel() (страховка 9.6) — после Close() токен
        // всегда отменён, даже при нормальном завершении (баг 9.9: «Анализ отменён» на успехе).
        bool cancelled = ct.IsCancellationRequested;
        progressDialog.Close();

        if (error != null)
        {
            StatusMessage = "Анализ не удался";
            return;
        }

        if (cancelled)
        {
            // отмена: состояние не меняем — KeyframeList прежний, Export не включаем
            StatusMessage = "Анализ отменён";
            return;
        }

        KeyframeList = keyframes;
        IsKeyframePanelExpanded = true;

        _isAnalyzed = true;
        foreach (var kf in KeyframeList)
            kf.PropertyChanged += OnKeyframePropertyChanged;
        ExportFileCommand.NotifyCanExecuteChanged();
        StatusMessage = $"Анализ завершён: {KeyframeList.Count(k => k.IsSelected)}/{KeyframeList.Count} ключевых кадров с лицом";
    }

    private bool CanAnalyzeFile() => SelectedFaceIndex >= 0;

    #endregion

    #region Export

    [RelayCommand(CanExecute = nameof(CanExportFile))]
    private async void ExportFile()
    {
        var segments = FFmpegService.BuildSegments(KeyframeList, _mediaPlayerService.DurationMs);
        if (segments.Count == 0)
        {
            StatusMessage = "Нет выбранных ключевых кадров";
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi",
            FileName = GetUniqueFileName(FilePath, "_FaceCut")
        };
        if (dlg.ShowDialog() != true)
            return;

        var progressDialog = new ProgressDialog("Экспорт видео", "Сборка отрезков...",
            indeterminate: false, maximum: segments.Count);
        progressDialog.Owner = Application.Current.MainWindow;

        // работа стартует ДО ShowDialog: код после ShowDialog выполнится только после его закрытия
        var task = Task.Run(() =>
        {
            // Progress<T> доставляет отчёты на UI-поток (захваченный SynchronizationContext)
            var progress = new Progress<int>(p =>
            {
                progressDialog.UpdateProgress(p);
                progressDialog.UpdateMessage($"Сборка отрезков... ({p}/{segments.Count})");
            });
            FFmpegService.ExportSegments(FilePath, dlg.FileName, segments, progress, progressDialog.Cts.Token);
        });

        // по завершении работы (успех или ошибка) закрываем диалог, если пользователь его ещё не закрыл
        _ = task.ContinueWith(_ => progressDialog.Close(), TaskScheduler.FromCurrentSynchronizationContext());

        try
        {
            progressDialog.ShowDialog(); // модально: основное окно заблокировано
            await task;
            StatusMessage = $"Сохранено: {dlg.FileName}";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Экспорт отменён";
        }
        catch (Exception ex)
        {
            StatusMessage = "Экспорт не удался";
            System.Diagnostics.Debug.WriteLine($"Export failed: {ex.Message}");
        }
        finally
        {
            progressDialog.Close(); // страховка; повторный Close — no-op
        }
    }

    private bool CanExportFile() => _isAnalyzed && KeyframeList.Any(k => k.IsSelected);

    /// Имя = оригинал + суффикс; при коллизии — «имя (1).ext», «имя (2).ext», … (конвенция Windows).
    private static string GetUniqueFileName(string originalPath, string suffix)
    {
        string dir = Path.GetDirectoryName(originalPath) ?? ".";
        string name = Path.GetFileNameWithoutExtension(originalPath) + suffix;
        string ext = Path.GetExtension(originalPath);

        string candidate = Path.Combine(dir, name + ext);
        int n = 1;
        while (File.Exists(candidate))
            candidate = Path.Combine(dir, $"{name} ({n++}){ext}");
        return candidate;
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

            _mediaPlayerService.Dispose();
            _faceDetector?.Dispose();
            _faceRecognizer?.Dispose();
        }

        _disposed = true;
    }

    #endregion
}