using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using MediaInfo;

namespace SmartVideoCutterWPF.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    #region Properties

    // << LibVLCSharp
    private readonly LibVLC? _libVlc;
    [ObservableProperty] private MediaPlayer? _mediaPlayer;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _maxPosition;
    private readonly DispatcherTimer _timer;
    private bool _isSeeking;
    private bool _wasPlayingBeforeSeek;
    private bool _isUpdatingFromTimer;
    // LibVLCSharp >>

    private readonly YoloFaceDetector? _faceDetector;

    private bool _disposed;

    #endregion

    public MainViewModel()
    {
        Core.Initialize(); // Инициализация нативных библиотек VLC
        _libVlc = new LibVLC();
        MediaPlayer = new MediaPlayer(_libVlc);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        _faceDetector = new YoloFaceDetector();
    }

    [RelayCommand]
    private void OpenFile()
    {
        var dlg = new OpenFileDialog { Filter = "Video files|*.avi;*.mov;*.mkv;*.mp4" };
        if (dlg.ShowDialog() == true)
        {
            _timer.Stop();
            MediaPlayer.Stop();

            using var media = new Media(_libVlc, new Uri(dlg.FileName));

            MediaPlayer.Media = media;

            Position = 0;
            MaxPosition = new MediaInfoWrapper(dlg.FileName).Duration;

            _timer.Start();
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var settingsViewModel = new SettingsViewModel();
        var settingsWindow = new SettingsWindow();

        settingsWindow.ShowDialog();
    }

    #region Player

    [RelayCommand]
    private void PlayPause()
    {
        if (MediaPlayer.IsPlaying)
        {
            MediaPlayer.Pause();
        }
        else
        {
            MediaPlayer.Play();
        }
    }

    [RelayCommand]
    private void DragStarted()
    {
        _isSeeking = true;

        _wasPlayingBeforeSeek = MediaPlayer.IsPlaying; // Запоминаем текущее состояние: играл плеер или стоял на паузе

        if (_wasPlayingBeforeSeek)
        {
            MediaPlayer.Pause(); // Ставим на паузу, чтобы звук не заикался при перемещении
        }
    }

    [RelayCommand]
    private void DragCompleted()
    {
        _isSeeking = false;

        // Возвращаем плееру то состояние, которое было до начала перемотки
        if (_wasPlayingBeforeSeek && MediaPlayer != null)
        {
            MediaPlayer.Play();
        }
    }

    private void Timer_Tick(object sender, EventArgs e)
    {
        // Опрашиваем плеер ТОЛЬКО если пользователь не занят перемоткой прямо сейчас
        if (!_isSeeking && MediaPlayer != null && MediaPlayer.IsPlaying)
        {
            _isUpdatingFromTimer = true;

            _position = MediaPlayer.Time;

            OnPropertyChanged(nameof(Position));

            _isUpdatingFromTimer = false;
        }
    }

    // Этот метод срабатывает КАЖДЫЙ РАЗ, когда пользователь сдвигает ползунок (даже на 1 пиксель)
    partial void OnPositionChanged(double value)
    {
        if (!_isUpdatingFromTimer && MediaPlayer != null)
        {
            MediaPlayer.Time = (long)value;
        }
    }

    #endregion

    [RelayCommand]
    private void AnalyzeFile()
    {
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
            // Останавливаем и уничтожаем таймер
            if (_timer != null)
            {
                _timer.Tick -= Timer_Tick; // Отписываемся от события
                _timer.Stop(); // Останавливаем поток таймера
            }

            // Освобождаем MediaPlayer
            if (_mediaPlayer != null)
            {
                if (_mediaPlayer.IsPlaying)
                {
                    _mediaPlayer.Stop();
                }

                // Отписываемся от событий MediaPlayer, если вы их добавляли (например, EndReached, TimeChanged)
                // _mediaPlayer.TimeChanged -= MediaPlayer_TimeChanged; 

                _mediaPlayer.Dispose();
            }

            // Освобождаем сам контекст LibVLC (строго ПОСЛЕ плеера)
            _libVlc?.Dispose();

            //_faceDetector?.Dispose();
        }

        _disposed = true;
    }

    // Деструктор на случай, если Dispose не был вызван вручную
    ~MainViewModel()
    {
        Dispose(false);
    }

    #endregion
}