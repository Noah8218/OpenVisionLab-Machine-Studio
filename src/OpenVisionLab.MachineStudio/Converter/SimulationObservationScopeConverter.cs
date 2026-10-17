using System.Globalization;
using System.Windows;
using System.Windows.Data;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio.Converter;

public sealed class SimulationObservationScopeConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length != 2 || values[1] == DependencyProperty.UnsetValue)
            return Array.Empty<SimulationSequenceObservation>();
        return values[0] switch
        {
            IReadOnlyList<SimulationSequenceObservation> rows => InScope(rows, values[1], row => row.UnitId),
            IReadOnlyList<EquipmentUnitActivityItem> units => InScope(units, values[1], unit => unit.UnitId),
            _ => Array.Empty<SimulationSequenceObservation>()
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => targetTypes.Select(_ => Binding.DoNothing).ToArray();

    private static object InScope<T>(IReadOnlyList<T> items, object? value, Func<T, string?> unitId)
    {
        if (value is null) return items;
        return value is string id && !string.IsNullOrWhiteSpace(id)
            ? items.Where(item => string.Equals(unitId(item), id, StringComparison.Ordinal)).ToArray()
            : Array.Empty<T>();
    }
}
