using System.Windows;

namespace SmartVideoCutterFlyleaf.Views;

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