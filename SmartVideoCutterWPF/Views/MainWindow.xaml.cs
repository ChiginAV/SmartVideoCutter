using System.Windows;
using System.Windows.Input;
using LibVLCSharp.Shared;

namespace SmartVideoCutterWPF.Views;

public partial class MainWindow : Window
{
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