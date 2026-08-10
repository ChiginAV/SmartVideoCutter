using System.Windows;
using System.Windows.Input;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using Microsoft.ML.OnnxRuntime;

namespace SmartVideoCutterWPF.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new MainViewModel();

    private readonly LibVLC? _libVlc;
    private readonly MediaPlayer? _mediaPlayer;
    private Media? _media;

    private string? _currentFile;

    private bool _isSliderDragging = false;

    public MainWindow()
    {
        InitializeComponent();

        // << LibVLCSharp
        Core.Initialize();

        _libVlc = new LibVLC();
        _mediaPlayer = new MediaPlayer(_libVlc);
        VideoView.MediaPlayer = _mediaPlayer;

        InitializePlayerEvents();
        // LibVLCSharp >>

        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
    }

    protected override void OnClosed(EventArgs e)
    {
        _mediaPlayer?.Stop();
        _media?.Dispose();
        _mediaPlayer?.Dispose();
        _libVlc?.Dispose();

        _viewModel?.Dispose();

        base.OnClosed(e);
    }

    #region Menu

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Video files|*.avi;*.mov;*.mkv;*.mp4" };

        if (dialog.ShowDialog(this) != true)
            return;

        Task.Run(() =>
        {
            _mediaPlayer?.Stop();

            _media?.Dispose();
            _media = null;

            Application.Current.Dispatcher.Invoke(() => { TimeSlider.Value = 0; });
        });

        _currentFile = dialog.FileName;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settingsViewModel = new SettingsViewModel();
        var settingsWindow = new SettingsWindow(settingsViewModel);

        settingsWindow.ShowDialog();
    }

    #endregion

    #region Player

    private void InitializePlayerEvents()
    {
        // Установка максимальной длины трека в миллисекундах
        _mediaPlayer?.LengthChanged += (sender, e) =>
        {
            Dispatcher.BeginInvoke(new Action(() => { TimeSlider.Maximum = e.Length; }));
        };

        // Обновление положения слайдера во время воспроизведения
        _mediaPlayer?.TimeChanged += (sender, e) =>
        {
            if (!_isSliderDragging)
            {
                Dispatcher.BeginInvoke(new Action(() => { TimeSlider.Value = e.Time; }));
            }
        };
    }

    // Пользователь зажал ползунок — отключаем автоматическое обновление
    private void TimeSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isSliderDragging = true;
    }

// Пользователь отпустил ползунок — перематываем видео и включаем обновление обратно
    private void TimeSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isSliderDragging = false;
        _mediaPlayer?.Time = (long)TimeSlider.Value;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFile))
        {
            MessageBox.Show("Сначала выберите файл", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_mediaPlayer != null)
        {
            if (_media == null)
            {
                _media = new Media(_libVlc!, _currentFile, FromType.FromPath);

                _mediaPlayer.Play(_media);

                TimeSlider.Maximum = _mediaPlayer.Length;
            }
            else
            {
                _mediaPlayer.Pause(); // vlc сам инвертирует действие play/pause 
            }
        }
    }

    #endregion

    private void Analyze_Click(object sender, RoutedEventArgs e)
    {
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
    }
}