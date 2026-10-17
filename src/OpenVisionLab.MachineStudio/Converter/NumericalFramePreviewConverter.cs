using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenVisionLab.Machine.Simulation.Camera;

namespace OpenVisionLab.MachineStudio.Converter;

public sealed class NumericalFramePreviewConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not NumericalCameraInput input) return null;
        if (parameter as string == "height")
        {
            if (input.Frame.PixelFormat != "HeightMapFloat32LE") return null;
            double minimum = input.Samples.Min(), span = Math.Max(input.Samples.Max() - minimum, double.Epsilon);
            var pixels = new byte[input.Samples.Count * 4];
            for (int index = 0; index < input.Samples.Count; index++)
            {
                double normalized = Math.Clamp((input.Samples[index] - minimum) / span, 0, 1);
                pixels[index * 4] = (byte)((1 - normalized) * 255);
                pixels[index * 4 + 1] = (byte)((1 - Math.Abs(normalized * 2 - 1)) * 255);
                pixels[index * 4 + 2] = (byte)(normalized * 255);
                pixels[index * 4 + 3] = 255;
            }
            var heightImage = BitmapSource.Create(input.Frame.Width, input.Frame.Height, 96, 96, PixelFormats.Bgra32, null, pixels, input.Frame.Width * 4);
            heightImage.Freeze();
            return heightImage;
        }
        var image = BitmapSource.Create(input.Frame.Width, input.Frame.Height, 96, 96, PixelFormats.Gray8, null, input.CopyPreview(), input.Frame.Width);
        image.Freeze();
        return image;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
