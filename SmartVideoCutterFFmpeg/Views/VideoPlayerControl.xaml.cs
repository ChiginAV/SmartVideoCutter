using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;


namespace SmartVideoCutterFFmpeg.Views;

/// <summary>
/// Контрол видео-плеера: кадр (BitmapSource) + оверлей рамок лиц + панель управления.
/// DataContext ожидается MainViewModel.
/// </summary>
public partial class VideoPlayerControl : UserControl
{
    private double _videoAspect;
    private bool _sliderPressed;
    private bool _wasPlaying; // видео играло в момент нажатия на слайдер

    // Заполнение внутренности рамки лица: alpha=1 (визуально невидимо).
    // С alpha=0 клики в прозрачной области проходят сквозь рамку;
    // с alpha=1 внутренность рамки кликабельна.
    private static readonly SolidColorBrush FaceBoxFill = CreateFaceBoxFill();

    private static SolidColorBrush CreateFaceBoxFill()
    {
        var brush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        brush.Freeze();
        return brush;
    }

    public VideoPlayerControl()
    {
        InitializeComponent();

        // Перемотка — только перетаскиванием бегунка.
        // Клик по треку полностью подавляем (см. OnSliderMouseDown).
        PositionSlider.PreviewMouseLeftButtonDown += OnSliderMouseDown;
        PositionSlider.PreviewMouseLeftButtonUp += OnSliderMouseUp;

        Loaded += OnLoaded;
    }

    private void OnSliderMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Если мышь не над бегунком — это клик по треку: подавляем событие,
        // чтобы Slider не сместил значение (LargeChange) и перемотка не
        // сработала. Единственный способ перемотки — перетаскивание бегунка.
        if (FindThumb(PositionSlider) is not Thumb thumb || !thumb.IsMouseOver)
        {
            e.Handled = true;
            return;
        }

        _sliderPressed = true;

        // Снимаем OneWay-биндинг Value←PositionMs: queued-обновление CurTime
        // из decode-потока (закэшированное до паузы) не должно возвращать
        // бегунок в позицию воспроизведения.
        BindingOperations.ClearBinding(PositionSlider, Slider.ValueProperty);

        // Пауза: PositionMs перестаёт обновляться, биндинг (даже queued)
        // не затрёт значение, которое двигает пользователь.
        if (DataContext is MainViewModel vm)
        {
            _wasPlaying = vm.IsPlaying;
            if (_wasPlaying)
                vm.Pause();
        }
    }

    private void OnSliderMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_sliderPressed)
            return;
        _sliderPressed = false;

        if (DataContext is not MainViewModel vm)
            return;

        int target = (int)PositionSlider.Value;
        vm.SeekToPosition(target);
        if (_wasPlaying)
            vm.Play();
        _wasPlaying = false;

        // Переподключаем биндинг: SeekTo синхронно обновил PositionMs до новой
        // позиции, поэтому бегунок останется на месте и снова будет следовать
        // за воспроизведением.
        PositionSlider.SetBinding(Slider.ValueProperty, new Binding(nameof(MainViewModel.PositionMs))
        {
            Mode = BindingMode.OneWay
        });
    }

    /// <summary>Ищет Thumb внутри шаблона Slider (не зависит от имени в шаблоне).</summary>
    private static Thumb? FindThumb(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Thumb thumb)
                return thumb;

            var found = FindThumb(child);
            if (found != null)
                return found;
        }

        return null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(this))
            return;

        VideoGrid.SizeChanged += (_, e) =>
        {
            UpdateOverlaySize(e.NewSize);
            DrawFaceBoxes();
        };

        if (DataContext is MainViewModel vm)
            vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.FaceBoxes)
            || e.PropertyName == nameof(MainViewModel.VideoAspect)
            || e.PropertyName == nameof(MainViewModel.SelectedFaceIndex))
        {
            UpdateOverlaySize(new Size(VideoGrid.ActualWidth, VideoGrid.ActualHeight));
            DrawFaceBoxes();
        }
    }

    private void DrawFaceBoxes()
    {
        if (DataContext is not MainViewModel vm)
            return;

        FaceOverlay.Children.Clear();

        // UpdateOverlaySize задаёт Width/Height синхронно, а ActualWidth/ActualHeight
        // обновляются только после следующего layout-прохода — поэтому читаем явные значения
        double w = FaceOverlay.Width, h = FaceOverlay.Height;
        if (double.IsNaN(w) || double.IsNaN(h))
        {
            w = FaceOverlay.ActualWidth;
            h = FaceOverlay.ActualHeight;
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

            FaceOverlay.Children.Add(rect);
        }
    }

    private void UpdateOverlaySize(Size ctrlSize)
    {
        if (DataContext is MainViewModel vm)
            _videoAspect = vm.VideoAspect;

        if (_videoAspect <= 0 || ctrlSize.Width <= 0 || ctrlSize.Height <= 0)
        {
            FaceOverlay.Width = FaceOverlay.Height = 0;
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

        FaceOverlay.Width = w;
        FaceOverlay.Height = h; // Grid сам выровняет Canvas по центру
    }
}