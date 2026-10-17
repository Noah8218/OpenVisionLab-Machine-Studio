using System.Globalization;
using System.Windows.Data;
using OpenVisionLab;

namespace OpenVisionLab.MachineStudio.Converter;

public sealed class RunEvidenceTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null || value is string text && string.IsNullOrWhiteSpace(text) ? OpenVisionLanguageService.T("RunHistory.Missing") : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
