using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaInfo;

namespace SmartVideoCutterVLC.Services;

public class MediaPlayerService : IDisposable
{
    #region Properties

    public const int DefaultVolume = 50;

    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private DispatcherTimer? _timer;
    private bool _isSeeking;
    private bool _wasPlayingBeforeSeek;
    private bool _isUpdatingFromTimer;
    private bool _disposed;

    private double _position;
    private double _maxPosition;

    public MediaPlayer? MediaPlayer => _mediaPlayer;
    public VideoInfo? VideoInfo { get; private set; }

    public double Position
    {
        get => _position;
        private set
        {
            _position = value;
            OnPropertyChanged(nameof(Position));
        }
    }

    public double MaxPosition
    {
        get => _maxPosition;
        private set
        {
            _maxPosition = value;
            OnPropertyChanged(nameof(MaxPosition));
        }
    }

    public double DetectionInterval => VideoInfo.Fps; // ~1 раз в секунду
    public bool IsPlaying => _mediaPlayer?.IsPlaying ?? false;

    public event Action? FrameReady; // вызывается когда доступен новый кадр
    public event PropertyChangedEventHandler? PropertyChanged;

    #endregion

    public void Initialize(Dispatcher dispatcher)
    {
        Core.Initialize();
        _libVlc = new LibVLC();
        _mediaPlayer = new MediaPlayer(_libVlc);
        _mediaPlayer.Volume = DefaultVolume;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    public void LoadMedia(string filePath)
    {
        _timer?.Stop();
        _mediaPlayer?.Stop();

        if (_libVlc != null)
        {
            using var media = new Media(_libVlc, new Uri(filePath));
            _mediaPlayer!.Media = media;

            // Получаем информацию о видео через ffprobe
            VideoInfo = FFmpegService.GetVideoInfo(filePath);

            var mediaInfoWrapper = new MediaInfoWrapper(filePath);

            Position = 0;
            MaxPosition = mediaInfoWrapper.Duration;

            _timer.Start();
        }
    }

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    #region Player

    public void Play() => _mediaPlayer?.Play();

    public void Pause() => _mediaPlayer?.Pause();

    public void PlayPause()
    {
        if (IsPlaying)
            Pause();
        else
            Play();
    }

    public void BeginSeek()
    {
        _isSeeking = true;
        _wasPlayingBeforeSeek = IsPlaying;

        if (_wasPlayingBeforeSeek)
            Pause();
    }

    public void EndSeek()
    {
        _isSeeking = false;

        if (_wasPlayingBeforeSeek && _mediaPlayer != null)
            Play();
    }

    public void SetPosition(double position)
    {
        Position = position;

        if (!_isUpdatingFromTimer && _mediaPlayer != null)
        {
            _mediaPlayer.Time = (long)position;
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        Debug.WriteLine($"Timer_Tick: seeking={_isSeeking}, playing={_mediaPlayer?.IsPlaying}");

        if (!_isSeeking && _mediaPlayer != null && _mediaPlayer.IsPlaying)
        {
            _isUpdatingFromTimer = true;
            Position = _mediaPlayer.Time;
            _isUpdatingFromTimer = false;

            Debug.WriteLine("FrameReady вызван");

            FrameReady?.Invoke();
        }
    }

    public void SetVolume(int volume) => _mediaPlayer?.Volume = volume;

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
            _timer?.Tick -= Timer_Tick;
            _timer?.Stop();

            _mediaPlayer?.Stop();
            _mediaPlayer?.Dispose();

            _libVlc?.Dispose();
        }

        _disposed = true;
    }

    ~MediaPlayerService() => Dispose(false);

    #endregion
}