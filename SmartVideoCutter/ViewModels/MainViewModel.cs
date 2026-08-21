using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartVideoCutter.Models;
using SmartVideoCutter.Models.Video;
using SmartVideoCutter.Services;
using SmartVideoCutter.Services.ComputerVision;
using SmartVideoCutter.Services.Video;


namespace SmartVideoCutter.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    #region Properties

    [ObservableProperty] private string _filePath = string.Empty;
    [ObservableProperty] private List<Keyframe> _keyframeList = new();
    [ObservableProperty] private bool _isKeyframePanelExpanded;
    [ObservableProperty] private string _statusMessage = "Откройте видео файл";

    private readonly MediaPlayerService _mediaPlayerService;
    private readonly DialogService _dialogs;
    private FaceDetector? _faceDetector;
    private FaceRecognizer? _faceRecognizer;
    private bool _disposed;
    private bool _isAnalyzed; // анализ выполнен — кнопка Export может активироваться

    /// <summary>Child-ViewModel плеера (DataContext для VideoPlayerControl).</summary>
    public PlayerViewModel Player { get; }

    public VideoInfo? VideoInfo => _mediaPlayerService.VideoInfo;

    #endregion

    public MainViewModel(DialogService dialogs)
    {
        _dialogs = dialogs;
        _mediaPlayerService = new MediaPlayerService();

        try
        {
            _faceDetector = new FaceDetector();
            _faceRecognizer = new FaceRecognizer(_faceDetector);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Face services init failed: {ex.Message}");
        }

        // Плеер инициализируется до создания контрола FlyleafME - рендер-поверхность создаётся один раз при загрузке
        // Если FFmpeg не настроен — no-op, OpenFile покажет сообщение.
        Player = new PlayerViewModel(_mediaPlayerService, _faceDetector, _faceRecognizer);
        Player.Initialize();
        if (Player.InitError != null)
            StatusMessage = "Ошибка инициализации плеера: " + Player.InitError;

        Player.FaceSelectionChanged += OnPlayerFaceSelectionChanged;
        Player.StatusMessageChanged += msg => StatusMessage = msg;
    }

    private void OnPlayerFaceSelectionChanged()
    {
        AnalyzeFileCommand.NotifyCanExecuteChanged(); // генератор не видит SelectedFaceIndex как зависимость
    }

    [RelayCommand]
    private void OpenFile()
    {
        var fileName = _dialogs.OpenVideoFile();
        if (fileName == null)
            return;

        if (!_mediaPlayerService.IsInitialized)
        {
            StatusMessage = "Укажите пути в настройках (Settings) и перезапустите приложение";
            return;
        }

        FilePath = fileName;

        Player.Reset();

        foreach (var kf in KeyframeList)
            kf.PropertyChanged -= OnKeyframePropertyChanged;

        _isAnalyzed = false;
        ExportFileCommand.NotifyCanExecuteChanged();

        KeyframeList = new List<Keyframe>();
        IsKeyframePanelExpanded = false;

        Player.LoadMedia(FilePath);
    }


    [RelayCommand]
    private void OpenSettings() => _dialogs.ShowSettings();

    partial void OnFilePathChanged(string? oldValue, string newValue)
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

    #region Face recognition

    [RelayCommand(CanExecute = nameof(CanAnalyzeFile))]
    private async Task AnalyzeFile()
    {
        if (!_mediaPlayerService.IsInitialized)
        {
            StatusMessage = "Укажите пути в настройках (Settings) и перезапустите приложение";
            return;
        }

        var recognizer = _faceRecognizer;
        var reference = Player.SelectedFace?.Embedding;
        var videoInfo = Player.VideoInfo;
        if (recognizer == null || reference == null || videoInfo == null)
            return;

        int w = videoInfo.Width, h = videoInfo.Height;

        // 1. Список ключевых кадров (ffprobe, быстро) — границы отрезков
        var keyframes = await Task.Run(() => FFmpegProbe.GetVideoKeyframes(FilePath));
        if (keyframes.Count == 0)
            return;

        double durationMs = Player.DurationMs;

        // 2. Окно с детерминированным прогрессом: maximum = число ключевых кадров
        var progress = new ProgressDialogViewModel("Анализ видео", "Поиск лица по кадрам...",
            indeterminate: false, maximum: keyframes.Count);
        var ct = progress.Cts.Token;

        // Вспомогательная строка: алгоритм + где идёт декод (GPU/CPU). ffprobe-проверка кодека
        // кэшируется — на уже открытом видео это быстро, но делаем в фоне всё равно.
        string algorithmName = SettingsManager.CurrentSettings.AnalysisAlgorithm ==
                               AppAnalysisAlgorithm.ThreeBetweenKeyframes
            ? "быстрый"
            : "точный";
        var filePath = FilePath;
        _ = Task.Run(() => progress.UpdateDetails(
            $"Алгоритм: {algorithmName} · {FFmpegProbe.DescribeDecoding(filePath)}"));

        // 3. Анализ кадров каждого отрезка [keyframe[i], keyframe[i+1]) — алгоритм из настроек.
        //    Логика в FaceRecognizer: нашли человека — отрезок принят, остальные кадры
        //    пропускаем (ранний выход). Отмена: выходим до следующей итерации.
        //    Работа стартует ДО ShowProgress: код после него выполнится только после закрытия окна.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var task = Task.Run(() =>
        {
            recognizer.Analyze(
                FilePath, keyframes, durationMs, w, h, videoInfo.Fps, reference,
                SettingsManager.CurrentSettings.AnalysisAlgorithm,
                i =>
                {
                    progress.UpdateProgress(i);
                    progress.UpdateMessage($"Поиск лица по кадрам... ({i}/{keyframes.Count})");
                },
                ct);
        });

        // по завершении работы (успех или ошибка) закрываем окно, если пользователь его ещё не закрыл
        _ = task.ContinueWith(_ => progress.RequestClose(), TaskScheduler.FromCurrentSynchronizationContext());

        try
        {
            _dialogs.ShowProgress(progress, modal: true); // модально: основное окно заблокировано
            await task;
            sw.Stop();
            var bench =
                $"[BENCH] Анализ: {sw.Elapsed.TotalSeconds:F1} c, отрезков: {keyframes.Count}, алгоритм: {SettingsManager.CurrentSettings.AnalysisAlgorithm}, threads: {Environment.GetEnvironmentVariable("SVC_FFMPEG_THREADS") ?? "2 (default)"}";
            Console.WriteLine(bench);
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "bench.log"),
                    bench + Environment.NewLine);
            }
            catch
            {
                /* не критично */
            }
        }
        catch (Exception ex)
        {
            // без catch исключение из ReadFrames упало бы в Task команды (необработанное)
            System.Diagnostics.Debug.WriteLine($"Analyze failed: {ex}");
            StatusMessage = "Анализ не удался";
            return;
        }
        finally
        {
            progress.RequestClose(); // страховка; повторный запрос — Close уже закрытого окна no-op
            FrameReader.ReleaseDecoders(); // внутрипроцессные декодеры больше не нужны
        }

        // Отмена — только по флагу VM: OnClosed → Cts.Cancel() (страховка) отменяет токен
        // после закрытия даже при успехе, ct.IsCancellationRequested здесь не использовать (баг 9.9).
        if (progress.IsCancelled)
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
        StatusMessage =
            $"Анализ завершён: {KeyframeList.Count(k => k.IsSelected)}/{KeyframeList.Count} ключевых кадров с лицом";
    }

    private bool CanAnalyzeFile() => Player.SelectedFaceIndex >= 0;

    #endregion

    #region Export

    [RelayCommand(CanExecute = nameof(CanExportFile))]
    private async Task ExportFile()
    {
        var segments = VideoExporter.BuildSegments(KeyframeList, Player.DurationMs);
        if (segments.Count == 0)
        {
            StatusMessage = "Нет выбранных ключевых кадров";
            return;
        }

        var outPath = _dialogs.SaveVideoFile(VideoExporter.GetUniqueFileName(FilePath, "_FaceCut"));
        if (outPath == null)
            return;

        var progress = new ProgressDialogViewModel("Экспорт видео", "Сборка отрезков...",
            indeterminate: false, maximum: segments.Count);
        var ct = progress.Cts.Token;

        // Видео — stream copy (без перекодирования), аудио — перекодирование для точной нарезки.
        string audioCodec = Path.GetExtension(outPath).Equals(".avi", StringComparison.OrdinalIgnoreCase)
            ? "MP3"
            : "AAC";
        progress.UpdateDetails($"Видео: без перекодирования · Аудио: перекодирование ({audioCodec})");

        // работа стартует ДО ShowProgress: код после него выполнится только после закрытия окна
        var task = Task.Run(() =>
        {
            // Progress<T> доставляет отчёты на UI-поток (захваченный SynchronizationContext)
            var report = new Progress<int>(p =>
            {
                progress.UpdateProgress(p);
                progress.UpdateMessage($"Сборка отрезков... ({p}/{segments.Count})");
            });
            VideoExporter.ExportSegments(FilePath, outPath, segments, report, ct);
        });

        // по завершении работы (успех или ошибка) закрываем окно, если пользователь его ещё не закрыл
        _ = task.ContinueWith(_ => progress.RequestClose(), TaskScheduler.FromCurrentSynchronizationContext());

        try
        {
            _dialogs.ShowProgress(progress, modal: true); // модально: основное окно заблокировано
            await task;
            StatusMessage = $"Сохранено: {outPath}";
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
            progress.RequestClose(); // страховка; повторный запрос — Close уже закрытого окна no-op
        }
    }

    private bool CanExportFile() => _isAnalyzed && KeyframeList.Any(k => k.IsSelected);

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
            Player.Dispose();
            _mediaPlayerService.Dispose();
            _faceDetector?.Dispose();
            _faceRecognizer?.Dispose();
        }

        _disposed = true;
    }

    #endregion
}