using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace SmartVideoCutterFFmpeg.Services;

/// <summary>
/// ОС-диалоги (выбор/сохранение файла, выбор папки), окно прогресса и окно настроек.
/// WPF-специфичный сервис — прецедент: <see cref="ThemeHelper"/>.
/// Stateless: создаётся там, где создаётся VM (MainWindow/SettingsWindow), т.к. DI-контейнера нет.
/// Единственное место, где создаются View-окна (ProgressDialog, SettingsWindow).
/// </summary>
public class DialogService
{
    private const string VideoOpenFilter = "Video files|*.avi;*.mov;*.mkv;*.mp4";

    /// <summary>Диалог выбора видеофайла. null — отмена.</summary>
    public string? OpenVideoFile()
    {
        var dlg = new OpenFileDialog { Filter = VideoOpenFilter };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>
    /// Диалог сохранения видеофайла с предложенным именем (без пути — путь задаётся
    /// через InitialDirectory). В фильтре показывается конкретное расширение исходного
    /// файла: экспорт идёт -c copy, поэтому контейнер сохраняется. null — отмена.
    /// </summary>
    public string? SaveVideoFile(string suggestedName)
    {
        string ext = Path.GetExtension(suggestedName);

        // первое расширение — контейнер исходника (экспорт -c copy его сохраняет),
        // остальные — для ручного выбора, без дублей
        var patterns = new List<string> { $"*{ext}" };
        foreach (var p in new[] { "*.mp4", "*.mkv", "*.mov", "*.avi" })
            if (!patterns.Contains(p))
                patterns.Add(p);

        var dlg = new SaveFileDialog
        {
            Filter = $"Video files ({ext})|{string.Join(";", patterns)}",
            FileName = Path.GetFileName(suggestedName),
            InitialDirectory = Path.GetDirectoryName(suggestedName) ?? string.Empty,
            DefaultExt = ext.TrimStart('.'),
            AddExtension = true
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>Диалог выбора папки. null — отмена.</summary>
    public string? OpenFolder()
    {
        var dlg = new OpenFolderDialog();
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    /// <summary>Показывает окно прогресса (создаёт View). modal — модальный режим (ShowDialog).</summary>
    public void ShowProgress(ProgressDialogViewModel viewModel, bool modal = false)
    {
        var window = new ProgressDialog(viewModel);
        window.Owner = Application.Current.MainWindow;
        if (modal)
            window.ShowDialog();
        else
            window.Show();
    }

    /// <summary>Открывает окно настроек (создаёт View) модально.</summary>
    public void ShowSettings()
    {
        var window = new SettingsWindow();
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
    }
}
