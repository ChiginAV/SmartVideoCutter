using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using FlyleafLib.Controls.WPF;
using System.Windows.Shapes;


namespace SmartVideoCutterFlyleaf.Views;

/// Interaction logic for MainWindow.xaml
public partial class MainWindow : Window
{
    private double _videoAspect;
    private Canvas _faceOverlay;

    // Заполнение внутренности рамки лица: alpha=1 (визуально невидимо).
    // В прозрачном окне Overlay клики в полностью прозрачных (alpha=0) областях
    // проходят сквозь окно вниз — с alpha=1 внутренность рамки кликабельна.
    private static readonly SolidColorBrush FaceBoxFill = CreateFaceBoxFill();

    private static SolidColorBrush CreateFaceBoxFill()
    {
        var brush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        brush.Freeze();
        return brush;
    }


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

        // Canvas рамок лиц. Не добавляем его в Grid с видео — видео рендерится
        // в отдельном Win32-окне (Surface), которое всегда поверх WPF-контента.
        // Canvas вешается в окно Overlay контрола (см. ниже).
        // ВАЖНО: IsHitTestVisible НЕ отключаем — это убивает hit-testing всего поддерева,
        // включая Rectangles. Background у Canvas = null (по умолчанию) → сам Canvas
        // клики в пустых областях НЕ перехватывает, управление плеером работает.
        var overlay = new Canvas();
        _faceOverlay = overlay;

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
                    // Вешаем Canvas рамок в окно Overlay — оно рендерится поверх видео.
                    // Шаблон оверлея FlyleafME не имеет ContentPresenter, поэтому присваивание Content не работает;
                    // добавляем в Grid из шаблона.
                    if (videoView.Overlay?.Template is { } template &&
                        template.FindName("PART_ContextMenuOwner", videoView.Overlay) is Grid overlayGrid)
                    {
                        overlayGrid.Children.Add(overlay);
                    }
                }));
            });

        // Контейнер: только видео.
        var container = new Grid();
        container.Children.Add(videoView);

        var vm = (MainViewModel)DataContext;
        container.SizeChanged += (_, e) =>
        {
            UpdateOverlaySize(overlay, e.NewSize);
            DrawFaceBoxes();
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.FaceBoxes)
                || e.PropertyName == nameof(MainViewModel.VideoAspect)
                || e.PropertyName == nameof(MainViewModel.SelectedFaceIndex))
            {
                UpdateOverlaySize(overlay, new Size(container.ActualWidth, container.ActualHeight), vm.VideoAspect);
                DrawFaceBoxes();
            }
        };

        PlayerContainer.Child = container;
    }

    private void DrawFaceBoxes()
    {
        if (_faceOverlay == null || DataContext is not MainViewModel vm)
            return;

        _faceOverlay.Children.Clear();

        // UpdateOverlaySize задаёт Width/Height синхронно, а ActualWidth/ActualHeight
        // обновляются только после следующего layout-прохода — поэтому читаем явные значения
        double w = _faceOverlay.Width, h = _faceOverlay.Height;
        if (double.IsNaN(w) || double.IsNaN(h))
        {
            w = _faceOverlay.ActualWidth;
            h = _faceOverlay.ActualHeight;
        }

        if (w <= 0 || h <= 0)
            return;

        for (int i = 0; i < vm.FaceBoxes.Count; i++)
        {
            var box = vm.FaceBoxes[i];
            bool isSelected = i == vm.SelectedFaceIndex;

            var rect = new Rectangle
            {
                Stroke = new SolidColorBrush(isSelected ? Colors.Red : Colors.LimeGreen),
                StrokeThickness = isSelected ? 3 : 2,
                Fill = FaceBoxFill, // внутренность рамки кликабельна (см. комментарий у поля)
                IsHitTestVisible = true,
                Width = box.W * w,
                Height = box.H * h
            };
            Canvas.SetLeft(rect, box.X * w);
            Canvas.SetTop(rect, box.Y * h);

            int capturedIndex = i;
            rect.MouseLeftButtonUp += (_, _) => vm.SelectFace(capturedIndex);

            _faceOverlay.Children.Add(rect);
        }
    }

    private void UpdateOverlaySize(Canvas overlay, Size ctrlSize, double? aspect = null)
    {
        if (aspect is double a) _videoAspect = a;
        if (_videoAspect <= 0 || ctrlSize.Width <= 0 || ctrlSize.Height <= 0)
        {
            overlay.Width = overlay.Height = 0;
            return;
        }

        // Emulate "Keep" aspect: вписываем видео в контрол с letterbox
        double w, h;
        if (_videoAspect > ctrlSize.Width / ctrlSize.Height)
        {
            w = ctrlSize.Width;
            h = w / _videoAspect;
        }
        else
        {
            h = ctrlSize.Height;
            w = h * _videoAspect;
        }

        overlay.Width = w;
        overlay.Height = h; // Grid сам выровняет Canvas по центру
    }
}