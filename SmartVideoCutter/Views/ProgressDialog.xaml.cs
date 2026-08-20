using System.Windows;
using SmartVideoCutter.Services;
using SmartVideoCutter.ViewModels;

namespace SmartVideoCutter.Views;

/// <summary>
/// Тонкое окно прогресса: состояние и логика — в <see cref="ProgressDialogViewModel"/>
/// (DataContext). Здесь только привязка и страховочная отмена при закрытии.
/// </summary>
public partial class ProgressDialog : Window
{
    private readonly ProgressDialogViewModel _viewModel;

    public ProgressDialog(ProgressDialogViewModel viewModel)
    {
        InitializeComponent();

        ThemeHelper.Attach(this); // заголовок окна следует за темой

        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.CloseRequested += () => Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        // страховка: окно закрыто любым способом (крестик, Esc, код) → отменяем работу.
        // Dispose() намеренно не вызываем: фоновый поток может ещё обращаться к токену;
        // CTS уйдёт в GC вместе с окном.
        _viewModel.Cts.Cancel();
        base.OnClosed(e);
    }
}
