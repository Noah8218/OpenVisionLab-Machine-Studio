using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OpenVisionLab.MachineStudio.Converter;

[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            var invert = parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase);
            return b ^ invert ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility visibility)
        {
            var invert = parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase);
            return (visibility == Visibility.Visible) ^ invert;
        }
        return false;
    }
}
