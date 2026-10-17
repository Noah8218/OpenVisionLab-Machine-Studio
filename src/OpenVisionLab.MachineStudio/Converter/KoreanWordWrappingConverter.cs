using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;

namespace OpenVisionLab.MachineStudio.Converter;

public sealed class KoreanWordWrappingConverter : IValueConverter
{
    // WPF의 한글 어절 내부 줄바꿈을 표시 문자열에서만 제한한다. 원본과 명령 인수는 그대로 둔다.
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string text
        ? Regex.Replace(text, "(?<=[가-힣])(?=[가-힣])", "\u2060") : value ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
