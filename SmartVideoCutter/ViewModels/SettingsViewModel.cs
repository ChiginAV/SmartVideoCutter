using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartVideoCutter.Models;
using SmartVideoCutter.Services;

namespace SmartVideoCutter.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly Settings _settings;
    private readonly DialogService _dialogs;
    [ObservableProperty] private bool _hasErrors;
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// Окно должно закрыться (поднимается после успешного сохранения).
    public event EventHandler? CloseRequested;

    public SettingsViewModel(DialogService dialogs, Settings? settings = null)
    {
        _dialogs = dialogs;
        _settings = settings ?? SettingsManager.CurrentSettings;
        _settings.PropertyChanged += (s, e) => { Validate(); };
    }

    /// Экземпляр настроек для привязки в XAML: Settings.FfmpegPath и т.д.
    public Settings Settings => _settings;

    /// Значения для ComboBox выбора темы.
    public AppThemeMode[] ThemeModes => Enum.GetValues<AppThemeMode>();

    /// Значения для ComboBox выбора алгоритма анализа.
    public AppAnalysisAlgorithm[] AnalysisAlgorithms => Enum.GetValues<AppAnalysisAlgorithm>();

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (HasErrors) return;

        SettingsManager.Save();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanSave() => !HasErrors;

    [RelayCommand]
    private void BrowseFfmpegFolder()
    {
        var folder = _dialogs.OpenFolder();
        if (folder != null)
            _settings.FfmpegPath = folder;
    }

    private void Validate()
    {
        var errors = new List<string>();
        var warnings = new List<string>();

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