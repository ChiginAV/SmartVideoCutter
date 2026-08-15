using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using FlyleafLib;
using FlyleafLib.MediaPlayer;

namespace SmartVideoCutterFlyleaf.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    #region Properties

    [ObservableProperty] private string _filePath = string.Empty;
    [ObservableProperty] private List<Keyframe> _keyframeList = new();
    [ObservableProperty] private bool _isKeyframePanelExpanded;

    private readonly MediaPlayerService _mediaPlayerService;
    private YoloFaceDetector? _faceDetector;
    private bool _disposed;

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
        _mediaPlayerService = new MediaPlayerService();
        _mediaPlayerService.Initialize(Dispatcher.CurrentDispatcher);
        _mediaPlayer = _mediaPlayerService.Player;

        //_faceDetector = new YoloFaceDetector();
    }

    [RelayCommand]
    private async void OpenFile()
    {
        var dlg = new OpenFileDialog { Filter = "Video files|*.avi;*.mov;*.mkv;*.mp4" };
        if (dlg.ShowDialog() != true)
            return;

        FilePath = dlg.FileName;

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

    [RelayCommand]
    private void SeekToTimestamp(Keyframe? keyframe)
    {
        if (keyframe != null)
        {
            _mediaPlayerService.Player.Seek((int)keyframe.Timestamp);
        }
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

    private bool CanAnalyzeFile() => !string.IsNullOrEmpty(FilePath);

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
        Генераторы в одном проходе не видят вывод друг друга → зависимость не отслеживается → CanExecute вычисляется один раз при создании (false) и никогда не обновляется.
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
            _mediaPlayerService.Dispose();
            _faceDetector?.Dispose();
        }

        _disposed = true;
    }

    #endregion
}