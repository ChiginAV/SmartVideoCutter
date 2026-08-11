using System.ComponentModel;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaInfo;

namespace SmartVideoCutterWPF.Services;

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

    public bool IsPlaying => _mediaPlayer?.IsPlaying ?? false;

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

            Position = 0;
            MaxPosition = new MediaInfoWrapper(filePath).Duration;

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
        if (!_isSeeking && _mediaPlayer != null && _mediaPlayer.IsPlaying)
        {
            _isUpdatingFromTimer = true;
            Position = _mediaPlayer.Time;
            _isUpdatingFromTimer = false;
        }
    }

    public void SetVolume(int volume)
    {
        _mediaPlayer?.Volume = volume;
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
            _timer?.Tick -= Timer_Tick;
            _timer?.Stop();

            _mediaPlayer?.Stop();
            _mediaPlayer?.Dispose();

            _libVlc?.Dispose();
        }

        _disposed = true;
    }

    ~MediaPlayerService()
    {
        Dispose(false);
    }

    #endregion
}