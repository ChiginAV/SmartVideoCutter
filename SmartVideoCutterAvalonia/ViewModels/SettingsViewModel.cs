using System;
using CommunityToolkit.Mvvm.Input;
using SmartVideoCutterAvalonia.Models;

namespace SmartVideoCutterAvalonia.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly Settings _settings = new();

    [ObservableProperty] private bool _hasErrors;
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// Окно должно закрыться (поднимается после успешного сохранения).
    public event EventHandler? CloseRequested;

    /// Экземпляр настроек для привязки в XAML: Settings.YoloPath и т.д.
    public Settings Settings => _settings;

    // --- Заглушки команд: функциональность перенесём позже ---

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
    }

    private bool CanSave() => !HasErrors;

    [RelayCommand]
    private void BrowseFfmpegFolder()
    {
    }

    [RelayCommand]
    private void BrowseYoloFile()
    {
    }

    [RelayCommand]
    private void BrowseArcFaceFile()
    {
    }
}