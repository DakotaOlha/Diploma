using System.Globalization;
using System.Windows.Data;

namespace Diploma.Helpers;

public sealed class EntryToCanvasLeftConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3) return 0.0;

        if (values[0] is not TimeSpan offset)   return 0.0;
        if (values[1] is not long durationMs)    return 0.0;
        if (values[2] is not double canvasWidth) return 0.0;

        if (durationMs <= 0 || canvasWidth <= 0) return 0.0;

        var ratio = offset.TotalMilliseconds / durationMs;
        var left  = ratio * canvasWidth;

        return Math.Clamp(left, 0.0, canvasWidth - 1.0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}