using System.Globalization;
using System.Windows.Data;

namespace OpenVisionLab.MachineStudio.Converter;

public sealed class InspectorSelectionValueConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value ?? Binding.DoNothing;
}
