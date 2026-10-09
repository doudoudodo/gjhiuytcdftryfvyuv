using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Coclico.Converters;

public class HexToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush FallbackBrush;

    static HexToBrushConverter()
    {
        FallbackBrush = new SolidColorBrush(Colors.Gray);
        FallbackBrush.Freeze();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SolidColorBrush> _cache = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex)
        {
            if (_cache.TryGetValue(hex, out SolidColorBrush? cached))
            {
                return cached;
            }

            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                _ = _cache.TryAdd(hex, brush);
                return brush;
            }
            catch
            {
                Services.LoggingService.LogError($"[HexToBrushConverter] Invalid hex color: {value}");
                return FallbackBrush;
            }
        }
        return FallbackBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
