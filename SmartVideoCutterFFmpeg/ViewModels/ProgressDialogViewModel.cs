using System.Threading;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SmartVideoCutterFFmpeg.ViewModels;

/// <summary>
/// Состояние и логика окна прогресса (<see cref="Views.ProgressDialog"/>).
/// Создаётся на UI-потоке; UpdateProgress/UpdateMessage могут вызываться
/// из фонового потока (маршализация в UI-диспетчер).
/// Показ окна — DialogService.ShowProgress (единственное место, где создаётся View).
/// </summary>
public partial class ProgressDialogViewModel : ObservableObject
{
    [ObservableProperty] private string _message;
    [ObservableProperty] private double _progressValue;

    /// Заголовок окна (константа диалога).
    public string Title { get; }

    /// Максимум шкалы (неопределённый режим — не используется).
    public double Maximum { get; }

    /// Неопределённый режим (бегущие огоньки).
    public bool IsIndeterminate { get; }

    /// Токен отмены: передаётся в метод, выполняющий работу.
    public CancellationTokenSource Cts { get; } = new();

    /// <summary>
    /// Отменено пользователем (кнопка «Отмена»/Esc). В отличие от токена,
    /// не выставляется страховочным Cts.Cancel() в OnClosed (баг 9.9).
    /// </summary>
    public bool IsCancelled { get; private set; }

    /// Окно должно закрыться (кнопка «Отмена» или код после завершения работы).
    public event Action? CloseRequested;

    private readonly Dispatcher _uiDispatcher;

    public ProgressDialogViewModel(string title, string message, bool indeterminate = true, double maximum = 100)
    {
        // VM создаётся на UI-потоке (команды MainViewModel) — диспетчер захватываем здесь.
        _uiDispatcher = Dispatcher.CurrentDispatcher;
        Title = title;
        _message = message;
        IsIndeterminate = indeterminate;
        Maximum = maximum;
    }

    /// <summary>Обновление прогресса (может вызываться из фонового потока).</summary>
    public void UpdateProgress(double value) => PostToUi(() => ProgressValue = value);

    /// <summary>Обновление текста (может вызываться из фоногого потока).</summary>
    public void UpdateMessage(string message) => PostToUi(() => Message = message);

    /// <summary>Кнопка «Отмена» (или Esc, т.к. IsCancel=True): отмена работы + закрытие окна.</summary>
    [RelayCommand]
    private void Cancel()
    {
        IsCancelled = true;
        Cts.Cancel();
        RequestClose();
    }

    /// <summary>Запрос закрытия окна (из кода VM после завершения работы).</summary>
    public void RequestClose() => CloseRequested?.Invoke();

    /// <summary>
    /// Неблокирующая отправка на UI-поток: фоновый цикл не должен ждать UI-поток
    /// и не должен падать, если приложение завершается.
    /// </summary>
    private void PostToUi(Action action)
    {
        if (_uiDispatcher.HasShutdownStarted || _uiDispatcher.HasShutdownFinished)
            return;

        if (_uiDispatcher.CheckAccess())
            action();
        else
            _uiDispatcher.BeginInvoke(action);
    }
}
