using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SmartVideoCutterFFmpeg.Views;

/// <summary>
/// Контрол видео-плеера: кадр + оверлей рамок лиц + панель управления.
/// DataContext ожидается PlayerViewModel (устанавливается в MainWindow.xaml).
/// Рамки лиц — XAML (ItemsControl + DataTemplate); здесь только letterbox-размер
/// оверлея и подавление клика по треку слайдера (перемотка — только бегунком).
/// Состояние перетаскивания (IsDragging) и пауза/перемотка — в PlayerViewModel.
/// </summary>
public partial class VideoPlayerControl : UserControl
{
    private double _videoAspect;
    private bool _sliderPressed;

    public VideoPlayerControl()
    {
        InitializeComponent();

        // Перемотка — только перетаскиванием бегунка.
        // Клик по треку полностью подавляем (см. OnSliderMouseDown).
        PositionSlider.PreviewMouseLeftButtonDown += OnSliderMouseDown;
        PositionSlider.PreviewMouseLeftButtonUp += OnSliderMouseUp;

        // Клик по рамке лица: событие пузырится из Rectangle (DataTemplate)
        // на ItemsControl. RelativeSource-биндинг команды в behavior внутри
        // DataTemplate не разрешается, поэтому вызов команды здесь.
        FaceOverlay.MouseLeftButtonUp += OnFaceOverlayMouseUp;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Клик по рамке лица → SelectFaceCommand (индекс берём из FaceBox).</summary>
    private void OnFaceOverlayMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Shapes.Rectangle rect)
            return; // клик мимо рамок (в пустую область оверлея)

        if (rect.DataContext is not FaceBox box)
            return;

        if (DataContext is PlayerViewModel vm)
            vm.SelectFaceCommand.Execute(box.Index);
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

        // Пауза + захват PositionMs — в VM (TwoWay-биндинг слайдера пишет
        // значение напрямую; CurTime-обновления игнорируются, пока IsDragging).
        if (DataContext is PlayerViewModel vm)
            vm.StartDraggingCommand.Execute(null);
    }

    private void OnSliderMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_sliderPressed)
            return;
        _sliderPressed = false;

        // Перемотка на позицию бегунка + возобновление — в VM.
        if (DataContext is PlayerViewModel vm)
            vm.EndDraggingCommand.Execute(null);
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

        VideoGrid.SizeChanged += OnVideoGridSizeChanged;

        // Aspect меняется при детекции лиц — пересчитываем letterbox-размер оверлея
        if (DataContext is PlayerViewModel vm)
            vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        VideoGrid.SizeChanged -= OnVideoGridSizeChanged;
        if (DataContext is PlayerViewModel vm)
            vm.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnVideoGridSizeChanged(object? sender, SizeChangedEventArgs e)
        => UpdateOverlaySize(e.NewSize);

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.VideoAspect))
            UpdateOverlaySize(new Size(VideoGrid.ActualWidth, VideoGrid.ActualHeight));
    }

    private void UpdateOverlaySize(Size ctrlSize)
    {
        if (DataContext is PlayerViewModel vm)
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
        FaceOverlay.Height = h; // Grid сам выровняет оверлей по центру
    }
}
