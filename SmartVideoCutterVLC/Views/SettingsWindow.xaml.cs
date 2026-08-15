using System.Windows;
using SmartVideoCutterVLC.ViewModels;

namespace SmartVideoCutterVLC.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        var settingsViewModel = new SettingsViewModel();
        settingsViewModel.OwnerWindow = this;

        DataContext = settingsViewModel;
    }
}