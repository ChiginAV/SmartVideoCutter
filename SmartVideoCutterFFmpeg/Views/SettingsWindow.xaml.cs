using System.Windows;

namespace SmartVideoCutterFFmpeg.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow()
    {
        InitializeComponent();

        ThemeHelper.Attach(this); // заголовок окна следует за темой

        _viewModel = new SettingsViewModel(new DialogService());
        _viewModel.CloseRequested += (s, e) => Close();
        DataContext = _viewModel;
    }
}