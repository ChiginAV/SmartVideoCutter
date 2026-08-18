using System.Windows;

namespace SmartVideoCutterFFmpeg.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow()
    {
        InitializeComponent();

        _viewModel = new SettingsViewModel();
        _viewModel.CloseRequested += (s, e) => Close();
        DataContext = _viewModel;
    }
}