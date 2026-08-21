using System;
using System.Threading.Tasks;
using System.Windows;
using SmartVideoCutter.Models;
using SmartVideoCutter.Services;
using SmartVideoCutter.ViewModels;


namespace SmartVideoCutter;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// Индекс словаря темы (палитры) в MergedDictionaries — заменяется при смене темы.
    private const int ThemeDictionaryIndex = 0;

    protected override void OnStartup(StartupEventArgs e)
    {
        SettingsManager.Load();

        // Словари уже загружены в App.xaml (Light — дефолт, Styles).
        // Заменяем палитру на сохранённую (Light/Dark).
        ApplyTheme(SettingsManager.CurrentSettings.ThemeMode);

        // Живое переключение темы при изменении настроек
        SettingsManager.CurrentSettings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Settings.ThemeMode))
                ApplyTheme(SettingsManager.CurrentSettings.ThemeMode);
        };

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
            // Событие приходит из финализатора/GC-потока — Application.MainWindow требует UI-поток
            // (иначе InvalidOperationException из Dispatcher.VerifyAccess). Маршализуем через Dispatcher.
            try
            {
                var ex = e.Exception;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (MainWindow?.DataContext is MainViewModel vm)
                        vm.StatusMessage = "Ошибка: " + ex.Message;
                }));
            }
            catch
            {
                /* приложение закрывается — не критично */
            }

            e.SetObserved();
        };
    }

    /// <summary>
    /// Применяет палитру темы, заменяя словарь на позиции ThemeDictionaryIndex.
    /// </summary>
    public static void ApplyTheme(AppThemeMode theme)
    {
        string themeFile = theme == AppThemeMode.Dark
            ? "Themes/Dark.xaml"
            : "Themes/Light.xaml";

        var dict = new ResourceDictionary { Source = new Uri(themeFile, UriKind.Relative) };

        var merged = Current.Resources.MergedDictionaries;
        if (merged.Count > ThemeDictionaryIndex)
            merged[ThemeDictionaryIndex] = dict;
        else
            merged.Insert(ThemeDictionaryIndex, dict);
    }
}