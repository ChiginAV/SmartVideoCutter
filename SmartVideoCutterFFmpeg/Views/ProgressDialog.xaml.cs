using System.Windows;

namespace SmartVideoCutterFFmpeg.Views;

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
        Application.Current.Dispatcher.Invoke(() => { ProgressBar.Value = value; });
    }

    // Метод для обновления текста
    public void UpdateMessage(string message)
    {
        Application.Current.Dispatcher.Invoke(() => { MessageTextBlock.Text = message; });
    }
}