using Avalonia.Controls;
using SmartVideoCutterAvalonia.ViewModels;

namespace SmartVideoCutterAvalonia.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow()
    {
        InitializeComponent();

        _viewModel = new SettingsViewModel();
        _viewModel.CloseRequested += (_, _) => Close();
        DataContext = _viewModel;
    }
}