using Avalonia.Controls;

namespace SmartVideoCutterAvalonia.Views;

public partial class ProgressDialog : Window
{
    public ProgressDialog(string title, string message, bool indeterminate = true, double maximum = 100)
    {
        InitializeComponent();

        Title = title;
        MessageTextBlock.Text = message;

        ProgressBar.IsIndeterminate = indeterminate;

        if (!indeterminate)
        {
            ProgressBar.Minimum = 0;
            ProgressBar.Maximum = maximum;
            ProgressBar.Value = 0;
        }
    }

    // Метод для обновления прогресса
    public void UpdateProgress(double value)
    {
        // Полная квалификация: Window.Dispatcher — инстанс-свойство,
        // а UIThread — статическое поле Avalonia.Threading.Dispatcher
        Avalonia.Threading.Dispatcher.UIThread.Invoke(() => ProgressBar.Value = value);
    }

    // Метод для обновления текста
    public void UpdateMessage(string message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Invoke(() => MessageTextBlock.Text = message);
    }
}