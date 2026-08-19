using System.Threading;
using System.Windows;

namespace SmartVideoCutterFFmpeg.Views;

public partial class ProgressDialog : Window
{
    /// Токен отмены: передаётся в метод, выполняющий работу.
    public CancellationTokenSource Cts { get; } = new();

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

        // клик по «Отмена» (или Esc, т.к. IsCancel=True) → отмена + закрытие
        CancelButton.Click += (_, _) =>
        {
            Cts.Cancel();
            Close();
        };
    }

    /// Метод для обновления прогресса
    public void UpdateProgress(double value)
    {
        PostToUi(() => { if (IsLoaded) ProgressBar.Value = value; });
    }

    /// Метод для обновления текста
    public void UpdateMessage(string message)
    {
        PostToUi(() => { if (IsLoaded) MessageTextBlock.Text = message; });
    }

    /// Неблокирующая отправка на UI-поток: фоновый цикл не должен ждать UI-поток
    /// и не должен падать, если окно уже закрыто или приложение завершается.
    private void PostToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return;
        dispatcher.BeginInvoke(action);
    }

    protected override void OnClosed(EventArgs e)
    {
        // страховка: окно закрыто любым способом (крестик, Esc, код) → отменяем работу.
        // Dispose() намеренно не вызываем: фоновый поток может ещё обращаться к токену;
        // CTS уйдёт в GC вместе с окном.
        Cts.Cancel();
        base.OnClosed(e);
    }
}
