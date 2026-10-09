using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Coclico.Converters;

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool v ? v ? Visibility.Collapsed : Visibility.Visible : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is Visibility vis ? (object)(vis != Visibility.Visible) : throw new NotSupportedException();
    }
}
