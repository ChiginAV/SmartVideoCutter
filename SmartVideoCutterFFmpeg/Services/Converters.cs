using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

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