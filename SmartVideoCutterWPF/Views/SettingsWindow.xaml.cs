using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SmartVideoCutterWPF.Views;

public partial class SettingsWindow : Window
{
    public SettingsViewModel ViewModel { get; }
    
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        
        DataContext = viewModel;
        ViewModel = viewModel;
    }

    private void BrowseFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is TextBox targetTextBox)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Файлы|*.onnx;*.pt;*.bin;*.*|Все файлы|*.*"
            };
            if (dialog.ShowDialog() == true)
                targetTextBox.Text = dialog.FileName;
        }
    }
    
    private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is TextBox targetTextBox)
        {
            var dialog = new OpenFolderDialog();
            if (dialog.ShowDialog() == true)
                targetTextBox.Text = dialog.FolderName;
        }
    }
    
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.YoloPath = txtYoloPath.Text;
        ViewModel.ArcFacePath = txtArcfacePath.Text;
        ViewModel.FfmpegPath = txtFfmpegPath.Text;
        
        ViewModel.Save();
        
        Close();
    }
}