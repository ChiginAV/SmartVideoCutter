using System.ComponentModel;
using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using OpenCvSharp;
using OpenCvSharp.Extensions;


namespace SmartVideoCutterFlyleaf.Services;

public class MediaPlayerService : IDisposable
{
    #region Properties

    public const int DefaultVolume = 50;

    private Player? _flyleafPlayer;
    private bool _disposed;

    public Player? Player => _flyleafPlayer;
    public VideoInfo? VideoInfo { get; private set; }

    public bool IsPlaying => _flyleafPlayer?.IsPlaying ?? false;

    public event PropertyChangedEventHandler? PropertyChanged;

    #endregion

    public void Initialize(Dispatcher dispatcher)
    {
        FlyleafLib.Engine.Start(new EngineConfig()
        {
            FFmpegPath = SettingsManager.CurrentSettings.FfmpegPath,
            //LogLevel = FlyleafLib.LogLevel.Debug,
            FFmpegHLSLiveSeek = true,
            UIRefresh = true,
            //FFmpegLoadProfile = Flyleaf.FFmpeg.LoadProfile.All
        });

        var playerConfig = new Config();
        playerConfig.Player.Usage = Usage.AVS;
        playerConfig.Player.AutoPlay = false;
        playerConfig.Player.SeekAccurate = true;

        _flyleafPlayer = new Player(playerConfig);
        _flyleafPlayer.Audio.Volume = DefaultVolume;
    }

    public void LoadMedia(string filePath)
    {
        _flyleafPlayer?.Stop();

        if (_flyleafPlayer != null)
        {
            // Загрузка файла через Flyleaf
            _flyleafPlayer.Open(filePath);

            // Получаем информацию о видео через ffprobe
            //VideoInfo = FFmpegService.GetVideoInfo(filePath);
        }
    }

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    #region Player

    public void Play() => _flyleafPlayer?.Play();

    public void Pause() => _flyleafPlayer?.Pause();

    public void PlayPause()
    {
        if (IsPlaying)
            Pause();
        else
            Play();
    }

    public void SetVolume(int volume) => _flyleafPlayer?.Audio.Volume = volume;

    /// <summary>
    /// Получает текущий кадр (последний отрендеренный) как OpenCvSharp Mat (BGR).
    /// Работает и во время паузы — снимок берётся из D3D11-текстуры рендера.
    /// </summary>
    public Mat? GetCurrentFrame()
    {
        if (_flyleafPlayer == null)
            return null;

        using var bitmap = _flyleafPlayer.TakeSnapshotToBitmap();
        if (bitmap == null)
            return null;

        var bgra = bitmap.ToMat(); // CV_8UC4 (32bpp BGRA)

        // BGRA → BGR: детектору нужны ровно 3 канала
        var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        bgra.Dispose();
        return bgr;
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
            _flyleafPlayer?.Stop();
            _flyleafPlayer?.Dispose();
        }

        _disposed = true;
    }

    ~MediaPlayerService() => Dispose(false);

    #endregion
}