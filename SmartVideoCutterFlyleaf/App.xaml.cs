using System.Configuration;
using System.Data;
using System.Windows;

namespace SmartVideoCutterFlyleaf;

/// Interaction logic for App.xaml
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        SettingsManager.Load();

        // глобальная обработка исключений
        DispatcherUnhandledException += (s, e) =>
        {
            if (MainWindow?.DataContext is MainViewModel vm)
                vm.StatusMessage = "Ошибка: " + e.Exception.Message;
            e.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            if (MainWindow?.DataContext is MainViewModel vm)
                vm.StatusMessage = "Ошибка: " + e.Exception.Message;
            e.SetObserved();
        };
    }
}