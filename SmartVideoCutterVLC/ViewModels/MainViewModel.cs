using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using SmartVideoCutterVLC.Models;
using SmartVideoCutterVLC.Services;
using SmartVideoCutterVLC.Views;

namespace SmartVideoCutterVLC.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    #region Properties

    private string _filePath;

    private readonly MediaPlayerService _mediaPlayerService;
    private YoloFaceDetector? _faceDetector;
    private bool _disposed;

    [ObservableProperty] private double _position;
    [ObservableProperty] private double _maxPosition;
    [ObservableProperty] private int _volume = MediaPlayerService.DefaultVolume;
    [ObservableProperty] private List<Keyframe> _keyframeList;

    public MediaPlayer? MediaPlayer => _mediaPlayerService.MediaPlayer;
    public VideoInfo? VideoInfo => _mediaPlayerService.VideoInfo;

    #endregion

    public MainViewModel()
    {
        _mediaPlayerService = new MediaPlayerService();
        _mediaPlayerService.Initialize(Dispatcher.CurrentDispatcher);
        _mediaPlayerService.PropertyChanged += MediaPlayerService_OnPropertyChanged;

        //_faceDetector = new YoloFaceDetector();
    }

    private void MediaPlayerService_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaPlayerService.Position))
            Position = _mediaPlayerService.Position;
        else if (e.PropertyName == nameof(MediaPlayerService.MaxPosition))
            MaxPosition = _mediaPlayerService.MaxPosition;
    }

    [RelayCommand]
    private async void OpenFile()
    {
        var dlg = new OpenFileDialog { Filter = "Video files|*.avi;*.mov;*.mkv;*.mp4" };
        if (dlg.ShowDialog() != true)
            return;

        _filePath = dlg.FileName;

        _mediaPlayerService.LoadMedia(_filePath);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var settingsWindow = new SettingsWindow();
        settingsWindow.ShowDialog();
    }

    #region Player

    [RelayCommand]
    private void PlayPause()
    {
        _mediaPlayerService.PlayPause();
    }

    [RelayCommand]
    private void DragStarted()
    {
        _mediaPlayerService.BeginSeek();
    }

    [RelayCommand]
    private void DragCompleted()
    {
        _mediaPlayerService.EndSeek();
    }

    // Этот метод срабатывает КАЖДЫЙ РАЗ, когда пользователь сдвигает ползунок (даже на 1 пиксель)
    partial void OnPositionChanged(double value)
    {
        _mediaPlayerService.SetPosition(value);
    }

    [RelayCommand]
    private void SeekToTimestamp(Keyframe? keyframe)
    {
        if (keyframe != null)
        {
            _mediaPlayerService.SetPosition(keyframe.Timestamp * 1000);
        }
    }

    partial void OnVolumeChanged(int value)
    {
        _mediaPlayerService.SetVolume(value);
    }

    #endregion

    [RelayCommand]
    private async void AnalyzeFile()
    {
        var progressDialog = new ProgressDialog("Анализ видео", "Получение ключевых кадров...");
        progressDialog.Owner = Application.Current.MainWindow;
        progressDialog.Show();

        KeyframeList = await Task.Run(() => FFmpegService.GetVideoKeyframes(_filePath));

        progressDialog.Close();
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
            _mediaPlayerService.PropertyChanged -= MediaPlayerService_OnPropertyChanged;
            _mediaPlayerService.Dispose();

            _faceDetector?.Dispose();
        }

        _disposed = true;
    }

    #endregion
}