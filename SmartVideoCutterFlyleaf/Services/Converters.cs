using System.Collections;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace SmartVideoCutterFlyleaf.Converters;

[ValueConversion(typeof(double), typeof(string))]
public class MillisecondsToTimeConverter : IValueConverter
{
    // double (мс) → "mm:ss.fff"; при >= 1 часа — "hh:mm:ss.fff"
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double ms || ms < 0)
            return string.Empty;

        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException(); // колонка read-only, обратное преобразование не нужно
}