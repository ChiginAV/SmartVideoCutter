using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace SmartVideoCutterFFmpeg.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly Settings _settings;
    [ObservableProperty] private bool _hasErrors;
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// Окно должно закрыться (поднимается после успешного сохранения).
    public event EventHandler? CloseRequested;

    public SettingsViewModel(Settings? settings = null)
    {
        _settings = settings ?? SettingsManager.CurrentSettings;
        _settings.PropertyChanged += (s, e) => { Validate(); };
    }

    /// Экземпляр настроек для привязки в XAML: Settings.YoloPath и т.д.
    public Settings Settings => _settings;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (HasErrors) return;

        SettingsManager.Save();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanSave() => !HasErrors;

    [RelayCommand]
    private void BrowseYoloFile()
    {
        var dialog = new OpenFileDialog { Filter = "ONNX-модели|*.onnx|Все файлы|*.*" };
        if (dialog.ShowDialog() == true)
            _settings.YoloPath = dialog.FileName;
    }

    [RelayCommand]
    private void BrowseArcFaceFile()
    {
        var dialog = new OpenFileDialog { Filter = "ONNX-модели|*.onnx|Все файлы|*.*" };
        if (dialog.ShowDialog() == true)
            _settings.ArcFacePath = dialog.FileName;
    }

    [RelayCommand]
    private void BrowseFfmpegFolder()
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog() == true)
            _settings.FfmpegPath = dialog.FolderName;
    }

    private void Validate()
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(_settings.YoloPath))
            warnings.Add("Не выбран файл YOLO");
        else if (!File.Exists(_settings.YoloPath))
            errors.Add("Файл YOLO не найден");

        if (string.IsNullOrWhiteSpace(_settings.ArcFacePath))
            warnings.Add("Не выбран файл ArcFace");
        else if (!File.Exists(_settings.ArcFacePath))
            errors.Add("Файл ArcFace не найден");

        if (string.IsNullOrWhiteSpace(_settings.FfmpegPath))
            warnings.Add("Не выбрана папка FFmpeg");
        else if (!Directory.Exists(_settings.FfmpegPath))
            errors.Add("Папка FFmpeg не найдена");
        else
        {
            if (!File.Exists(Path.Combine(_settings.FfmpegPath, "ffmpeg.exe")))
                errors.Add("ffmpeg.exe не найден в папке FFmpeg");
            if (!File.Exists(Path.Combine(_settings.FfmpegPath, "ffprobe.exe")))
                errors.Add("ffprobe.exe не найден в папке FFmpeg");
        }

        HasErrors = errors.Count > 0;
        StatusMessage = string.Join("\n", errors.Concat(warnings));
        SaveCommand.NotifyCanExecuteChanged();
    }
}