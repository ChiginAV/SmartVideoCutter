using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using OpenCvSharp;


namespace SmartVideoCutterFlyleaf.ViewModels;

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

    private readonly MediaPlayerService _mediaPlayerService;
    private YoloFaceDetector? _faceDetector;
    private int _faceAnalysisSeq;
    private bool _disposed;
    private long _lastCurTime;
    private SelectedFace? _selectedFace; // данные выбора (кроп) для ArcFace
    private readonly Dispatcher _uiDispatcher;


    private Player? _mediaPlayer;

    public Player? MediaPlayer
    {
        get => _mediaPlayer;
        set => SetProperty(ref _mediaPlayer, value);
    }

    public VideoInfo? VideoInfo => _mediaPlayerService.VideoInfo;

    #endregion

    public MainViewModel()
    {
        _uiDispatcher = Dispatcher.CurrentDispatcher;

        _mediaPlayerService = new MediaPlayerService();
        _mediaPlayerService.Initialize(Dispatcher.CurrentDispatcher);
        _mediaPlayer = _mediaPlayerService.Player;

        if (_mediaPlayer != null)
        {
            _mediaPlayer.PropertyChanged += OnPlayerPropertyChanged;
            _mediaPlayer.PlaybackStopped += OnPlaybackStopped;
            _mediaPlayer.SeekCompleted += OnSeekCompleted;
        }

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
    private async void OpenFile()
    {
        var dlg = new OpenFileDialog { Filter = "Video files|*.avi;*.mov;*.mkv;*.mp4" };
        if (dlg.ShowDialog() != true)
            return;

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

    #region Player

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Player.Status))
        {
            StatusMessage = _mediaPlayer?.Status.ToString() ?? "—";

            if (_mediaPlayer?.Status == Status.Paused)
            {
                ClearFaceSelection(); // новый кадр — сбрасываем выбор
                _lastCurTime = _mediaPlayer?.CurTime ?? 0;
                _ = AnalyzeFaces();
            }
            else
            {
                FaceBoxes = new List<FaceBox>(); // Play/Stop: убираем рамки с экрана
                ClearFaceSelection();
            }
        }
        else if (e.PropertyName == nameof(Player.CurTime))
        {
            long t = _mediaPlayer?.CurTime ?? 0;
            bool changed = t != _lastCurTime;
            _lastCurTime = t;

            // Начало перемотки на паузе: убираем устаревшие рамки
            // (новые появятся после SeekCompleted)
            if (changed && _mediaPlayer?.Status == Status.Paused)
            {
                FaceBoxes = new List<FaceBox>();
                ClearFaceSelection();
            }
        }
    }


    private void OnSeekCompleted(object? sender, int ms)
    {
        // Событие приходит из фонового потока seek — маршализуем в UI
        _uiDispatcher.BeginInvoke(() =>
        {
            if (ms < 0)
                return; // перемотка не удалась

            if (_mediaPlayer?.Status == Status.Paused)
                _ = AnalyzeFaces(); // перемотка закончилась на паузе — рисуем новые рамки
        });
    }

    private void OnPlaybackStopped(object? sender, EventArgs e)
    {
        _uiDispatcher.BeginInvoke(() =>
        {
            StatusMessage = _mediaPlayer?.Status == Status.Failed
                ? "Ошибка воспроизведения"
                : "Воспроизведение завершено";
        });
    }


    [RelayCommand]
    private void SeekToTimestamp(Keyframe? keyframe)
    {
        if (keyframe != null)
        {
            _mediaPlayerService.Player.SeekAccurate((int)keyframe.Timestamp);
        }
    }

    #endregion

    #region Face detection

    private async Task AnalyzeFaces()
    {
        var detector = _faceDetector;
        var frame = _mediaPlayerService.GetCurrentFrame();
        int seq = ++_faceAnalysisSeq;

        if (detector == null || frame == null)
            return;

        // Запоминаем размеры ДО Task.Run — после Dispose к Mat нельзя обращаться
        double w = frame.Width, h = frame.Height;

        List<OpenCvSharp.Rect> rects;
        try
        {
            // Mat живёт всё время Task.Run, но освобождается один раз, после
            rects = await Task.Run(() => detector.Detect(frame));
        }
        catch (Exception ex)
        {
            frame.Dispose();
            System.Diagnostics.Debug.WriteLine($"Face detection failed: {ex.Message}");
            return;
        }

        frame.Dispose();

        // Игнорируем устаревший результат: уже нажали play или сделали новый seek
        if (seq != _faceAnalysisSeq || _mediaPlayer?.Status != Status.Paused)
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
            _selectedFace = new SelectedFace(crop, boxPx, _mediaPlayer?.CurTime ?? 0);
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

    [RelayCommand(CanExecute = nameof(CanAnalyzeFile))]
    private async void AnalyzeFile()
    {
        var progressDialog = new ProgressDialog("Анализ видео", "Получение ключевых кадров...");
        progressDialog.Owner = Application.Current.MainWindow;
        progressDialog.Show();

        KeyframeList = await Task.Run(() => FFmpegService.GetVideoKeyframes(FilePath));

        IsKeyframePanelExpanded = true;

        progressDialog.Close();
    }

    private bool CanAnalyzeFile() => SelectedFaceIndex >= 0;

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
            if (_mediaPlayer != null)
            {
                _mediaPlayer.PropertyChanged -= OnPlayerPropertyChanged;
            }

            _mediaPlayerService.Dispose();
            _faceDetector?.Dispose();
        }

        _disposed = true;
    }

    #endregion
}