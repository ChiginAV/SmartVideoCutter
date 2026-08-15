using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using FlyleafLib.Controls.WPF;

namespace SmartVideoCutterFlyleaf;

/// Interaction logic for MainWindow.xaml
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        DataContext = new MainViewModel();

        InitializeFlyleafPlayer();

        Closed += OnClosed; // Подписываемся на закрытие окна
    }

    private void OnClosed(object sender, EventArgs e)
    {
        if (DataContext is IDisposable disposableViewModel)
        {
            disposableViewModel.Dispose();
        }
    }

    private void InitializeFlyleafPlayer()
    {
        if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(this))
            return;

        var videoView = new FlyleafME();

        Binding playerBinding = new Binding("MediaPlayer")
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        };
        videoView.SetBinding(FlyleafME.PlayerProperty, playerBinding);

        // Применяем тему, когда к контролу будет привязан Player
        System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(FlyleafME.PlayerProperty, typeof(FlyleafME))
            .AddValueChanged(videoView, (_, _) =>
            {
                // оверлей создаётся при привязке плеера — даём Dispatcher'у дособраться
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    videoView.UIConfig ??= new UIConfig(videoView);
                    videoView.SelectedTheme = new UITheme
                    {
                        Name = "Custom",
                        PrimaryColor = Colors.DimGray,
                        SecondaryColor = Colors.DarkGray,
                        BackgroundColor = Color.FromRgb(32, 32, 32),
                        SurfaceColor = Colors.Black
                    };
                }));
            });

        PlayerContainer.Child = videoView;
    }
}