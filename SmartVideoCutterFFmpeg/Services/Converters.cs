using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace SmartVideoCutterFFmpeg.Converters;

[ValueConversion(typeof(double), typeof(string))]
public class MillisecondsToTimeConverter : IValueConverter
{
    // double/long (мс) → "mm:ss.fff"; при >= 1 часа — "hh:mm:ss.fff"
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not IConvertible convertible)
            return string.Empty;

        double ms = convertible.ToDouble(culture);
        if (ms < 0)
            return string.Empty;

        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException(); // колонка read-only, обратное преобразование не нужно
 }

/// <summary>
/// Отображение алгоритма анализа: enum → человекочитаемое имя для ComboBox.
/// </summary>
public class AnalysisAlgorithmDisplayNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            AppAnalysisAlgorithm.ThreePerSecond => "Точный (3 раза в секунду)",
            AppAnalysisAlgorithm.ThreeBetweenKeyframes => "Быстрый (3 раза между ключевыми кадрами)",
            _ => value?.ToString() ?? string.Empty
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException(); // ComboBox выбирает по объекту, обратное преобразование не нужно
}

/// <summary>
/// Умножение двух чисел (для MultiBinding): нормализованная координата рамки (0..1)
/// × размер оверлея в пикселях → пиксели.
/// </summary>
public class MultiplyConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2
            && values[0] is IConvertible a
            && values[1] is IConvertible b)
            return a.ToDouble(culture) * b.ToDouble(culture);

        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Цвет рамки лица: (Index, SelectedFaceIndex) → красный (выбрано) / зелёный (нет).
/// </summary>
public class FaceBoxBrushConverter : IMultiValueConverter
{
    private static readonly Brush Selected = Frozen(Colors.Red);
    private static readonly Brush Normal = Frozen(Colors.LimeGreen);

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool selected = values.Length >= 2
            && values[0] is IConvertible a
            && values[1] is IConvertible b
            && a.ToInt32(culture) == b.ToInt32(culture);

        return selected ? Selected : Normal;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Толщина рамки лица: (Index, SelectedFaceIndex) → 3 (выбрано) / 2 (нет).
/// </summary>
public class FaceBoxThicknessConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool selected = values.Length >= 2
            && values[0] is IConvertible a
            && values[1] is IConvertible b
            && a.ToInt32(culture) == b.ToInt32(culture);

        return selected ? 3.0 : 2.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}