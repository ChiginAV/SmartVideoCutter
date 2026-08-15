using System.Windows;
using SmartVideoCutterVLC.ViewModels;

namespace SmartVideoCutterVLC.Views;

public partial class MainWindow : Window
{
    private MainViewModel? ViewModel => DataContext as MainViewModel;

    public MainWindow()
    {
        InitializeComponent();

        DataContext = new MainViewModel();

        Closed += OnClosed; // Подписываемся на закрытие окна
    }

    private void OnClosed(object sender, EventArgs e)
    {
        if (DataContext is IDisposable disposableViewModel)
        {
            disposableViewModel.Dispose();
        }
    }
}