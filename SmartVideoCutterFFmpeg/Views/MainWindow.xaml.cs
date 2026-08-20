using System.Windows;

namespace SmartVideoCutterFFmpeg.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ThemeHelper.Attach(this); // заголовок окна следует за темой

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