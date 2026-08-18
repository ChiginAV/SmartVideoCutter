using System.Configuration;
using System.Data;
using System.Windows;

namespace SmartVideoCutterFlyleaf;

/// Interaction logic for App.xaml
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        SettingsManager.Load();
        /* Заготовка для нормальной реализации темной темы
        string themeFile = (SettingsManager.CurrentSettings.ThemeMode == AppThemeMode.Dark)
            ? "Themes/Dark.xaml"
            : "Themes/Light.xaml";

        Resources.MergedDictionaries.Add(
            new ResourceDictionary { Source = new Uri(themeFile, UriKind.Relative) });

        Resources.MergedDictionaries.Add(
            new ResourceDictionary { Source = new Uri("Themes/Styles.xaml", UriKind.Relative) });
        */

        base.OnStartup(e);

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