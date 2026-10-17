using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OpenVisionLab.MachineStudio.Converter;

public sealed class SceneSelectionWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double width ? width * 0.6 : DependencyProperty.UnsetValue;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
